using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APTM.Gate.Api.Services;

/// <summary>
/// Bridge between PostgreSQL LISTEN/NOTIFY and browser SSE connections.
/// Maintains a dedicated NpgsqlConnection (not from EF pool) for listening.
/// </summary>
public sealed class SseNotificationService : BackgroundService
{
    private readonly string _connectionString;
    private readonly ILogger<SseNotificationService> _logger;
    private readonly ConcurrentDictionary<Guid, HttpResponse> _clients = new();

    public SseNotificationService(IConfiguration configuration, ILogger<SseNotificationService> logger)
    {
        _connectionString = configuration.GetConnectionString("GateDb")
            ?? throw new InvalidOperationException("GateDb connection string not configured");
        _logger = logger;
    }

    /// <summary>
    /// Called by the SSE endpoint to register a new client and stream events.
    /// Blocks until the client disconnects.
    /// </summary>
    public async Task StreamEvents(HttpResponse response, CancellationToken ct)
    {
        var clientId = Guid.NewGuid();
        _clients.TryAdd(clientId, response);
        _logger.LogDebug("SSE client {ClientId} connected. Total: {Count}", clientId, _clients.Count);

        try
        {
            // Send initial keepalive
            await response.WriteAsync(": connected\n\n", ct);
            await response.Body.FlushAsync(ct);

            // Block until client disconnects
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _logger.LogDebug("SSE client {ClientId} disconnected. Total: {Count}", clientId, _clients.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SseNotificationService starting PostgreSQL LISTEN loop");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenLoop(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PostgreSQL LISTEN connection lost. Reconnecting in 3s...");
                await Task.Delay(3000, stoppingToken);
            }
        }

        _logger.LogInformation("SseNotificationService stopped");
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        conn.Notification += (_, e) =>
        {
            var sseEvent = $"event: {e.Channel}\ndata: {e.Payload}\n\n";
            _ = BroadcastToClientsAsync(sseEvent);
        };

        await using (var cmd = new NpgsqlCommand(
            "LISTEN tag_event; LISTEN race_start; LISTEN sync_data; LISTEN config_updated;", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Listening on PostgreSQL channels: tag_event, race_start, sync_data, config_updated");

        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct);
        }
    }

    private async Task BroadcastToClientsAsync(string sseEvent)
    {
        var deadClients = new List<Guid>();

        foreach (var (clientId, response) in _clients)
        {
            try
            {
                await response.WriteAsync(sseEvent);
                await response.Body.FlushAsync();
            }
            catch
            {
                deadClients.Add(clientId);
            }
        }

        foreach (var id in deadClients)
            _clients.TryRemove(id, out _);
    }
}
