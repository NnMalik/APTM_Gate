namespace APTM.Gate.Infrastructure.Entities;

public class RaceStartTime
{
    public Guid Id { get; set; }
    public Guid HeatId { get; set; }
    public int HeatNumber { get; set; }
    public DateTimeOffset GunStartTime { get; set; }
    public DateTimeOffset OriginalGunStartTime { get; set; }
    public Guid SourceDeviceId { get; set; }
    public Guid[] CandidateIds { get; set; } = [];
    public int SourceClockOffsetMs { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Operator group that started this heat. Soft-link to <see cref="OperatorGroupEntity"/> —
    /// nullable for legacy heats started before the grouping feature, and for tests
    /// configured with no operator groups (decision #1: "no group = legacy mode").
    /// Population is purely descriptive; finish-gate matching still keys on
    /// <c>CandidateIds</c> roster membership, which is unambiguous when groups are
    /// disjoint.
    /// </summary>
    public Guid? GroupId { get; set; }
}
