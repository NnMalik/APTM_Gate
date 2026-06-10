using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

/// <summary>
/// Runs EF Core migrations and installs PostgreSQL NOTIFY triggers.
/// Runs as a BackgroundService so it does NOT block Kestrel from starting.
/// </summary>
public sealed class PostgresInitService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresInitService> _logger;

    public PostgresInitService(IServiceProvider sp, IConfiguration configuration, ILogger<PostgresInitService> logger)
    {
        _sp = sp;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let Kestrel bind the port first
        await Task.Yield();

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                _logger.LogInformation("PostgresInitService: attempt {Attempt} — applying migrations...", attempt);

                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();

                // Test DB connection first
                if (!await db.Database.CanConnectAsync(stoppingToken))
                {
                    _logger.LogWarning("PostgresInitService: cannot connect to database. Retrying in 5s...");
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                // Apply pending migrations
                await db.Database.MigrateAsync(stoppingToken);
                _logger.LogInformation("PostgresInitService: migrations applied successfully");

                // Apply NOTIFY triggers
                var sql = GetTriggerSql();
                await db.Database.ExecuteSqlRawAsync(sql, stoppingToken);
                _logger.LogInformation("PostgresInitService: NOTIFY triggers applied");

                // Pre-flash identity seeding: if appsettings carries a Gate:Identity:Role and the
                // gate has never been provisioned, seed it now so a freshly-imaged NUC (especially
                // a remote checkpoint) comes up with its role/name already set — no field visit needed.
                await SeedPreflashIdentityAsync(scope, stoppingToken);

                return; // Success — exit
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgresInitService: attempt {Attempt} failed", attempt);
                if (attempt < 5)
                    await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogError("PostgresInitService: all 5 attempts failed. App will run with limited functionality.");
    }

    /// <summary>
    /// Seeds <c>gate_identity</c> from <c>Gate:Identity:*</c> in configuration when the gate has
    /// not yet been provisioned. No-op when no role is configured or an identity already exists —
    /// a field-set identity is never overwritten.
    /// </summary>
    private async Task SeedPreflashIdentityAsync(IServiceScope scope, CancellationToken ct)
    {
        var role = _configuration["Gate:Identity:Role"];
        if (string.IsNullOrWhiteSpace(role))
            return; // No pre-flash configured — leave provisioning to the field app.

        var svc = scope.ServiceProvider.GetRequiredService<IGateIdentityService>();

        // Never clobber an existing identity (field-set or previously seeded).
        if (await svc.GetAsync(ct) is not null)
        {
            _logger.LogInformation("PostgresInitService: identity already provisioned — skipping pre-flash seed.");
            return;
        }

        int? sequence = int.TryParse(_configuration["Gate:Identity:CheckpointSequence"], out var seq) ? seq : null;
        var name = _configuration["Gate:Identity:Name"];

        var result = await svc.SetAsync(
            new SetGateIdentityRequest { Role = role.Trim(), CheckpointSequence = sequence, Name = name },
            setBy: "preflash",
            force: false,
            ct);

        if (result.Success)
            _logger.LogInformation(
                "PostgresInitService: pre-flash identity seeded — role={Role}, sequence={Sequence}, name={Name}",
                role, sequence, name);
        else
            _logger.LogWarning("PostgresInitService: pre-flash identity seed rejected — {Error}", result.Error);
    }

    private static string GetTriggerSql()
    {
        var assembly = typeof(PostgresInitService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "APTM.Gate.Infrastructure.Persistence.init_triggers.sql");

        if (stream is null)
            throw new InvalidOperationException("Embedded resource init_triggers.sql not found.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
