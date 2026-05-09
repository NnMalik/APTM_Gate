namespace APTM.Gate.Infrastructure.Entities;

/// <summary>
/// Marks a heat as fully completed. Written at the finish gate when the last
/// candidate of a heat's roster crosses the line, OR via a manual force-close
/// from the field app for DNF cases. Replicated to the start gate via the
/// existing /gate/sync/push channel (dataType="heat_completion") so both displays
/// can freeze their timer at the same moment.
/// </summary>
public class HeatCompletion
{
    public Guid Id { get; set; }

    /// <summary>Matches <see cref="RaceStartTime.HeatId"/>. Unique — one completion per heat.</summary>
    public Guid HeatId { get; set; }

    public int HeatNumber { get; set; }

    /// <summary>Roster size at race-start time. 0 for force-close when roster wasn't known.</summary>
    public int ExpectedCount { get; set; }

    /// <summary>Distinct first-read finishers from the roster. Equals ExpectedCount on auto-completion;
    /// can be less than ExpectedCount on force-close (DNF).</summary>
    public int FinishedCount { get; set; }

    /// <summary>The candidate whose finish read tripped completion. Null on force-close.</summary>
    public Guid? LastCandidateId { get; set; }

    /// <summary>For auto-completion: the read_time of the last finish. For force-close: wall-clock at close.</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>"auto" | "force_close" | "timeout" (future).</summary>
    public string ClosureReason { get; set; } = "auto";

    /// <summary>Device code of the gate that computed (or received) this row.</summary>
    public string SourceDeviceCode { get; set; } = default!;

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
