namespace APTM.Gate.Core.Models;

public sealed class ProcessedEventDto
{
    public long Id { get; set; }
    /// <summary>Null for checkpoint events.</summary>
    public Guid? CandidateId { get; set; }
    public string TagEpc { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public int? EventId { get; set; }
    public DateTimeOffset ReadTime { get; set; }
    public decimal? DurationSeconds { get; set; }
    public int? CheckpointSequence { get; set; }
    public int? HeatNumber { get; set; }
    public bool IsFirstRead { get; set; }
    public string? CandidateName { get; set; }
    public int? JacketNumber { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>
    /// The heat (UUID) this finish belongs to. Null for checkpoint events and for
    /// UNRESOLVED finishes the gate couldn't match to a roster. Lets the puller
    /// (Field app / APTM Main) drop every event of a cancelled heat by HeatId —
    /// even rows pulled before the heat was cancelled, which an incremental pull
    /// can never re-send.
    /// </summary>
    public Guid? HeatId { get; set; }

    /// <summary>
    /// True once an HHT-issued race_cancel or heat_candidate_remove has voided this
    /// finish. The puller must exclude voided rows from results.
    /// </summary>
    public bool Voided { get; set; }

    /// <summary>
    /// Processing status. "UNRESOLVED" = a finish read that matched no active heat
    /// roster (stray read / false start); null = a normal resolved event. Surfaced
    /// so the puller can keep UNRESOLVED rows out of results.
    /// </summary>
    public string? Status { get; set; }
}
