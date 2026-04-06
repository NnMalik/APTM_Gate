using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<PostgresInitService> _logger;

    public PostgresInitService(IServiceProvider sp, ILogger<PostgresInitService> logger)
    {
        _sp = sp;
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
