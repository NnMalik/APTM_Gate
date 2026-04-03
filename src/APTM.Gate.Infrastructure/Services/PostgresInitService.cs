using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

public sealed class PostgresInitService : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PostgresInitService> _logger;

    public PostgresInitService(IServiceProvider sp, ILogger<PostgresInitService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();

        // Apply pending migrations
        _logger.LogInformation("Applying pending EF Core migrations...");
        await db.Database.MigrateAsync(ct);
        _logger.LogInformation("Migrations applied.");

        // Apply NOTIFY triggers
        _logger.LogInformation("Applying PostgreSQL NOTIFY triggers...");
        var sql = GetTriggerSql();
        await db.Database.ExecuteSqlRawAsync(sql, ct);
        _logger.LogInformation("NOTIFY triggers applied.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

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
