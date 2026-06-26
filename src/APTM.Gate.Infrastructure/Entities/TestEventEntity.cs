namespace APTM.Gate.Infrastructure.Entities;

public class TestEventEntity
{
    public int TestTypeEventId { get; set; }
    public string EventName { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public int EventId { get; set; }
    public int Sequence { get; set; }
    public int? ScoringTypeId { get; set; }

    /// <summary>"SPRINT" | "PARALLEL" — how the display runs this event's heats. From config
    /// (Main already resolved the per-event flag / type default). Null on legacy gate data.</summary>
    public string? DisplayMode { get; set; }

    public ScoringTypeEntity? ScoringType { get; set; }
}
