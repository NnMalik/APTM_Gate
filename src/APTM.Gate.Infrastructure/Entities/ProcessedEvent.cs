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

    /// <summary>
    /// Set to true when the row has been retroactively invalidated — currently
    /// only by an HHT-issued race_cancel or heat_candidate_remove. Voided rows
    /// are preserved (audit trail) but excluded from:
    ///   • The display query (finish reads list, finished count).
    ///   • The dedup window check in BufferProcessingService — so a re-fired
    ///     heat with the same candidates can record fresh finish times.
    ///   • The tag_event NOTIFY trigger — see init_triggers.sql.
    /// </summary>
    public bool Voided { get; set; }

    /// <summary>
    /// Denormalized from <see cref="RaceStartTime.GroupId"/> at finish-match time.
    /// Lets the per-group display counters be a fast indexed query instead of a join.
    /// NULL for: legacy rows, checkpoint events (no heat to source from), and
    /// <c>UNRESOLVED</c> finishes (no matched heat).
    /// </summary>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// The heat (UUID) this finish belongs to, copied from <see cref="RaceStartTime.HeatId"/>
    /// when the buffer processor matches a finish to a heat. This is the *load-bearing*
    /// identifier — replacing <see cref="HeatNumber"/> in queries that void or count
    /// per-heat events. <c>HeatNumber</c> stays as a display label, scoped per-group
    /// via the (groupId, heatNumber) uniqueness on Main.
    ///
    /// NULL for: checkpoint events (no heat), legacy rows pre-Phase-7, and finishes
    /// the gate couldn't match to any heat's roster (see <see cref="Status"/> = UNRESOLVED).
    /// </summary>
    public Guid? HeatId { get; set; }

    /// <summary>
    /// Optional outcome label for finish events that couldn't be cleanly matched.
    /// Values: NULL (matched / not applicable) | "UNRESOLVED" (finish read but no
    /// heat's roster contained the candidate). UNRESOLVED rows are excluded from
    /// the display feed and the dedup window; they exist purely so admins can
    /// reconcile after the test (e.g. via a "review unresolved" UI on Main).
    ///
    /// Replaces the legacy <c>?? raceStarts[0]</c> fallback in the buffer processor:
    /// instead of guessing which heat a stray read belongs to, we record it as
    /// UNRESOLVED and let humans decide. See DESIGN_OPERATOR_GROUPS.md §6.3.
    /// </summary>
    public string? Status { get; set; }

    public CandidateEntity? Candidate { get; set; }
    public RawTagBuffer? RawTag { get; set; }
}
