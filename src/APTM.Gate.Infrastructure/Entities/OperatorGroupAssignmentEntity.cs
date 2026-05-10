namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// Authoritative device-to-group assignments shipped from Main via the config package.
/// The gate doesn't enforce these at scan time (the HHT's local filter is the primary
/// gate) — but it does expose them via <c>GET /gate/operator-groups</c> so HHTs can
/// detect overlaps when picking their selection (decision #3).
/// </summary>
public class OperatorGroupAssignmentEntity
{
    public Guid GroupId { get; set; }

    /// <summary>
    /// Device code (e.g. "HHT-01"), not Device.Id. The gate stores codes for everything
    /// because that's the identifier flowing in over the wire — the auth handler
    /// validates the X-Device-Token against the device code, and config-package
    /// payloads use codes too.
    /// </summary>
    public string DeviceCode { get; set; } = default!;
}
