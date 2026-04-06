namespace APTM.Gate.Core.Models;

public sealed class ProcessedEventDto
{
    public long Id { get; set; }
    public Guid CandidateId { get; set; }
    public string TagEpc { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public int? EventId { get; set; }
    public DateTimeOffset ReadTime { get; set; }
    public decimal? DurationSeconds { get; set; }
    public int? CheckpointSequence { get; set; }
    public bool IsFirstRead { get; set; }
    public string? CandidateName { get; set; }
    public int? JacketNumber { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
