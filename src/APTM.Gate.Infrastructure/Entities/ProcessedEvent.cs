namespace APTM.Gate.Infrastructure.Entities;

public class ProcessedEvent
{
    public long Id { get; set; }
    public Guid CandidateId { get; set; }
    public string TagEPC { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public DateTimeOffset ReadTime { get; set; }
    public decimal? DurationSeconds { get; set; }
    public int? CheckpointSequence { get; set; }
    public bool IsFirstRead { get; set; } = true;
    public long? RawBufferId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;

    public CandidateEntity Candidate { get; set; } = default!;
    public RawTagBuffer? RawTag { get; set; }
}
