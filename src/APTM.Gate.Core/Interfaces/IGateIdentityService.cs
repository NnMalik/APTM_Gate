using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

/// <summary>
/// Read/write access to the single-row gate_identity table that holds this NUC's
/// predefined role. Set once by the field app via PUT /gate/identity.
/// </summary>
public interface IGateIdentityService
{
    /// <summary>Returns the current identity, or null if the gate has never been provisioned.</summary>
    Task<GateIdentityInfo?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts the identity row.
    /// - First-time provisioning succeeds.
    /// - Repeating the same role/sequence is a no-op (idempotent).
    /// - Different role without <paramref name="force"/> returns Conflict.
    /// - With <paramref name="force"/> = true, purges raw_tag_buffer and active-event processed_events
    ///   before swapping role to prevent stale reads bleeding across role semantics.
    /// </summary>
    /// <param name="setBy">Token label / NameIdentifier from auth context — for audit trail.</param>
    Task<SetIdentityResult> SetAsync(SetGateIdentityRequest request, string setBy, bool force, CancellationToken ct = default);
}
