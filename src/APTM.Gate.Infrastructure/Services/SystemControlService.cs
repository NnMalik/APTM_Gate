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

    private readonly ILogger<SystemControlService> _logger;
    private readonly string _powerOffCommand;

    public SystemControlService(IConfiguration configuration, ILogger<SystemControlService> logger)
    {
        _logger = logger;
        var configured = configuration["Gate:PowerOffCommand"];
        _powerOffCommand = string.IsNullOrWhiteSpace(configured)
            ? DefaultPowerOffCommand
            : configured.Trim();
    }

    public Task PowerOffAsync(CancellationToken ct = default)
    {
        // First whitespace-delimited token is the executable, the remainder is args.
        var parts = _powerOffCommand.Split(' ', 2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fileName = parts[0];
        var arguments = parts.Length > 1 ? parts[1] : string.Empty;

        _logger.LogWarning(
            "Powering off the NUC via '{Command}'. The machine must be physically powered back on.",
            _powerOffCommand);

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
            _logger.LogError(ex, "Failed to launch power-off command '{Command}'.", _powerOffCommand);
        }

        return Task.CompletedTask;
    }
}
