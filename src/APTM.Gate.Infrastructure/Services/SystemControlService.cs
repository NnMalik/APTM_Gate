using System.Diagnostics;
using APTM.Gate.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

/// <summary>
/// Runs the OS power-off command on the NUC. The gate service runs as root (see the
/// systemd unit created by deploy/installer-template/setup.sh), so it can invoke
/// <c>systemctl poweroff</c> directly with no sudoers/polkit rule. The command is
/// configurable via <c>Gate:PowerOffCommand</c> so it can be pointed at a harmless
/// command while testing the endpoint path.
/// </summary>
public sealed class SystemControlService : ISystemControlService
{
    private const string DefaultPowerOffCommand = "systemctl poweroff";

    // Scheduled out-of-process via a transient systemd timer so the restart survives this process
    // being killed by the very `systemctl restart` it issued (a plain self-restart can race and
    // abort). Override with Gate:RestartCommand (e.g. on Windows, a Restart-Service invocation).
    private const string DefaultRestartCommand = "systemd-run --on-active=2 --collect systemctl restart aptm-gate";

    private readonly ILogger<SystemControlService> _logger;
    private readonly string _powerOffCommand;
    private readonly string _restartCommand;

    public SystemControlService(IConfiguration configuration, ILogger<SystemControlService> logger)
    {
        _logger = logger;
        var configured = configuration["Gate:PowerOffCommand"];
        _powerOffCommand = string.IsNullOrWhiteSpace(configured)
            ? DefaultPowerOffCommand
            : configured.Trim();

        var restart = configuration["Gate:RestartCommand"];
        _restartCommand = string.IsNullOrWhiteSpace(restart)
            ? DefaultRestartCommand
            : restart.Trim();
    }

    public async Task<bool> SetSystemTimeAsync(DateTimeOffset utc, CancellationToken ct = default)
    {
        // timedatectl set-time interprets its string in the machine's local timezone, so
        // format the absolute instant into local time (device tz is set to Asia/Kolkata at
        // install). The instant itself is unambiguous; only the rendering is local.
        var localStamp = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        _logger.LogWarning("Setting NUC clock to {Local} (local) / {Utc:o} (utc).", localStamp, utc);

        // NTP must be off or `set-time` is rejected and would be overwritten. Offline gate →
        // timesyncd can never sync anyway, so disabling it is safe and idempotent.
        await RunAsync("timedatectl", "set-ntp false", ct);
        return await RunAsync("timedatectl", $"set-time \"{localStamp}\"", ct);
    }

    /// <summary>Runs a command to completion (service runs as root — no sudo needed). Returns true on exit 0.</summary>
    private async Task<bool> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true
            });
            if (proc is null)
            {
                _logger.LogError("Failed to start '{File} {Args}' — null process.", fileName, arguments);
                return false;
            }

            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                var err = await proc.StandardError.ReadToEndAsync(ct);
                _logger.LogError("'{File} {Args}' exited {Code}: {Err}", fileName, arguments, proc.ExitCode, err);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run '{File} {Args}'.", fileName, arguments);
            return false;
        }
    }

    public Task PowerOffAsync(CancellationToken ct = default)
    {
        FireAndForget(_powerOffCommand,
            "Powering off the NUC via '{Command}'. The machine must be physically powered back on.");
        return Task.CompletedTask;
    }

    public Task RestartServiceAsync(CancellationToken ct = default)
    {
        FireAndForget(_restartCommand,
            "Restarting the gate service via '{Command}'. The service will be back in a few seconds.");
        return Task.CompletedTask;
    }

    /// <summary>Launch a configured OS command fire-and-forget. Failures are logged, never thrown.</summary>
    private void FireAndForget(string command, string logTemplate)
    {
        // First whitespace-delimited token is the executable, the remainder is args.
        var parts = command.Split(' ', 2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fileName = parts[0];
        var arguments = parts.Length > 1 ? parts[1] : string.Empty;

        _logger.LogWarning(logTemplate, command);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            // Log and swallow — this runs as a fire-and-forget task with no caller
            // left to surface the error to, and throwing would just fault that task.
            _logger.LogError(ex, "Failed to launch command '{Command}'.", command);
        }
    }
}
