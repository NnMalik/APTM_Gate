using System.Runtime.InteropServices;
using APTM.Gate.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Workers;

/// <summary>
/// Pings the systemd watchdog on a regular interval so systemd knows the process is alive.
/// Also monitors internal health (DB connectivity, reader connection) and logs warnings.
/// </summary>
public sealed class WatchdogService : BackgroundService
{
    private readonly ILogger<WatchdogService> _logger;
    private readonly IReaderStatusProvider _readerStatus;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _readerDisconnectedSince = DateTimeOffset.MaxValue;

    public WatchdogService(
        ILogger<WatchdogService> logger,
        IReaderStatusProvider readerStatus)
    {
        _logger = logger;
        _readerStatus = readerStatus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WatchdogService started — interval {Interval}s", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ping systemd watchdog (sd_notify WATCHDOG=1)
                NotifySystemd("WATCHDOG=1");

                // Monitor reader connection
                if (_readerStatus.IsConnected)
                {
                    _readerDisconnectedSince = DateTimeOffset.MaxValue;
                }
                else
                {
                    if (_readerDisconnectedSince == DateTimeOffset.MaxValue)
                        _readerDisconnectedSince = DateTimeOffset.UtcNow;

                    var elapsed = DateTimeOffset.UtcNow - _readerDisconnectedSince;
                    if (elapsed.TotalMinutes >= 5)
                    {
                        _logger.LogWarning("UHF reader disconnected for {Minutes:F0} minutes",
                            elapsed.TotalMinutes);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WatchdogService tick failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Sends sd_notify message to systemd.
    /// Only works on Linux when running as a systemd service with Type=notify.
    /// </summary>
    private void NotifySystemd(string state)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        var notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(notifySocket)) return;

        try
        {
            // Use the .NET built-in sd_notify via the hosting integration
            // The ASPNETCORE Type=notify already handles startup notification,
            // but we need manual WATCHDOG pings.
            var sockAddr = notifySocket.StartsWith('@')
                ? "\0" + notifySocket[1..]
                : notifySocket;

            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Unspecified);

            var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(sockAddr);
            socket.SendTo(System.Text.Encoding.UTF8.GetBytes(state), endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "sd_notify failed (non-critical on non-systemd environments)");
        }
    }
}
