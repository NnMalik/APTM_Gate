namespace APTM.Gate.Infrastructure.Entities;

public class RaceStartTime
{
    public Guid Id { get; set; }
    public Guid HeatId { get; set; }
    public int HeatNumber { get; set; }
    public DateTimeOffset GunStartTime { get; set; }
    public DateTimeOffset OriginalGunStartTime { get; set; }
    public Guid SourceDeviceId { get; set; }

    /// <summary>
    /// Friendly code of the HHT that started this heat (e.g. "HHT-02"), taken from the push
    /// envelope. Two HHTs each number their heats from 1, so the display falls back to this to
    /// distinguish concurrent heats when no operator-group name is available. Nullable for legacy
    /// rows created before this column.
    /// </summary>
    public string? SourceDeviceCode { get; set; }
    public Guid[] CandidateIds { get; set; } = [];
    public int SourceClockOffsetMs { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The HHT→gate clock offset (ms) actually applied to convert OriginalGunStartTime
    /// into GunStartTime. With a measured offset this is the value from the push payload;
    /// with the legacy receipt heuristic it's (ReceivedAt − OriginalGunStartTime).
    /// Null for rows created before this audit existed.
    /// </summary>
    public long? AppliedOffsetMs { get; set; }

    /// <summary>
    /// How GunStartTime was derived: "measured" (NTP-style offset carried in the push —
    /// immune to push delay) or "receipt" (legacy: assumes the push arrived instantly,
    /// so any queue/retry delay shortens the heat's elapsed times). Null for legacy rows.
    /// </summary>
    public string? OffsetMethod { get; set; }

    /// <summary>
    /// Operator group that started this heat. Soft-link to <see cref="OperatorGroupEntity"/> —
    /// nullable for legacy heats started before the grouping feature, and for tests
    /// configured with no operator groups (decision #1: "no group = legacy mode").
    /// Population is purely descriptive; finish-gate matching still keys on
    /// <c>CandidateIds</c> roster membership, which is unambiguous when groups are
    /// disjoint.
    /// </summary>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// The event (TestEvent.EventId) this heat belongs to. Sent by the HHT in the
    /// race_start payload; falls back to the gate's active event at receipt when an
    /// older HHT omits it. Lets the finish processor match a finish read to the gun
    /// start of the *same* event — critical when a candidate runs multiple events.
    /// Nullable for back-compat with rows created before event scoping.
    /// </summary>
    public int? EventId { get; set; }

    /// <summary>
    /// The test instance this heat belongs to, stamped from the gate's active config
    /// at receipt. Nullable for legacy rows. Scopes race-start lookups so stale starts
    /// from a previous test instance never leak into a new one.
    /// </summary>
    public Guid? TestInstanceId { get; set; }
}
