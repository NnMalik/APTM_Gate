namespace APTM.Gate.Infrastructure.Entities;

public class TestEventEntity
{
    public int TestTypeEventId { get; set; }
    public string EventName { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public int EventId { get; set; }
    public int Sequence { get; set; }
    public int? ScoringTypeId { get; set; }

    public ScoringTypeEntity? ScoringType { get; set; }
}
