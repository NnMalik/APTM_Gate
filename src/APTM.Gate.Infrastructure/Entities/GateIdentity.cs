namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// Single-row table (Id always = 1) holding the predefined role of this NUC.
/// Set once by the field app via PUT /gate/identity, persists across restarts,
/// and is used at startup to decide which workers to register and which display to serve.
/// </summary>
public class GateIdentity
{
    public int Id { get; set; } = 1;

    /// <summary>One of <see cref="APTM.Gate.Core.Enums.GateRole"/> as a string: Start | Checkpoint | Finish.</summary>
    public string Role { get; set; } = default!;

    /// <summary>Required iff <see cref="Role"/> == Checkpoint; null otherwise.</summary>
    public int? CheckpointSequence { get; set; }

    /// <summary>
    /// Operator-facing name/label for this gate (e.g. "River Bend Checkpoint"). Optional.
    /// Set at pre-flash (appsettings) or by the field app via PUT /gate/identity. Renaming
    /// does not require a service restart.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Should match Gate:DeviceCode in appsettings — sanity field for cross-checking.</summary>
    public string DeviceCode { get; set; } = default!;

    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Token label from <c>DeviceTokenAuthHandler</c> at the time identity was set — audit trail.</summary>
    public string SetBy { get; set; } = default!;
}
