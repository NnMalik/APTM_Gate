namespace APTM.Gate.Core.Interfaces;

/// <summary>
/// Host/OS-level control for the NUC the gate service runs on. Kept behind an
/// interface so lifecycle endpoints don't shell out to the OS directly — the Api
/// layer stays testable and the Core layer stays dependency-free (interface only).
/// </summary>
public interface ISystemControlService
{
    /// <summary>
    /// Powers the NUC off at the OS level. Fire-and-forget: the OS teardown will
    /// stop this process, so callers must already have flushed their HTTP response
    /// before invoking this.
    /// </summary>
    Task PowerOffAsync(CancellationToken ct = default);

    /// <summary>
    /// Restarts the gate service process at the OS level (systemd unit / Windows service).
    /// Fire-and-forget: the service manager stops this process and starts a fresh one, so callers
    /// must flush their HTTP response before invoking. Used to bring readers/workers online after
    /// first-time provisioning — workers register their role only at startup. The restart is
    /// scheduled out-of-process (systemd-run timer by default) so this process dying mid-call
    /// doesn't abort it.
    /// </summary>
    Task RestartServiceAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the NUC's system clock to the given absolute instant. Disables NTP first
    /// (these gates are offline, so timesyncd can't sync and would otherwise fight the
    /// set) then applies the time. The instant is timezone-independent — the device
    /// formats it to its own local zone for the OS command. Returns true on success.
    /// </summary>
    Task<bool> SetSystemTimeAsync(DateTimeOffset utc, CancellationToken ct = default);
}
