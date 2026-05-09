namespace APTM.Gate.Infrastructure.Entities;

public class ProcessedEvent
{
    public long Id { get; set; }

    /// <summary>
    /// Resolved candidate ID. Null for Checkpoint events — checkpoint gates have no
    /// tag_assignments and never resolve EPCs to candidates. Always set for Finish
    /// and Start_attendance events (those rely on the active config-package).
    /// </summary>
    public Guid? CandidateId { get; set; }

    public string TagEPC { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public int? EventId { get; set; }
    public DateTimeOffset ReadTime { get; set; }
    public decimal? DurationSeconds { get; set; }
    public int? CheckpointSequence { get; set; }
    public int? HeatNumber { get; set; }
    public bool IsFirstRead { get; set; } = true;
    public string? CandidateName { get; set; }
    public int? JacketNumber { get; set; }
    public long? RawBufferId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;

    public CandidateEntity? Candidate { get; set; }
    public RawTagBuffer? RawTag { get; set; }
}
