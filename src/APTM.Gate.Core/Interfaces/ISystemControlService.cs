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
}
