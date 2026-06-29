namespace APTM.Gate.Core.Models;

public sealed class SyncPullResponse
{
    public List<ProcessedEventDto> ProcessedEvents { get; set; } = [];
    public List<ReceivedSyncDataDto> ReceivedSyncData { get; set; } = [];
    public List<RaceStartTimeDto> RaceStartTimes { get; set; } = [];
    public List<HeatCompletionDto> HeatCompletions { get; set; } = [];
    public long HighWaterMark { get; set; }
    public long? SyncDataHighWaterMs { get; set; }

    /// <summary>
    /// The highest processed_events.id currently on the gate (0 when empty). Lets the
    /// puller detect a wiped/reset gate: after /gate/race-data/clear the BIGSERIAL
    /// restarts at 1, so a client whose cached high-water mark exceeds this value is
    /// asking for ids that will never exist again — it must reset its mark and re-pull
    /// from 0. Without this check, pulls after a wipe silently return empty forever.
    /// </summary>
    public long MaxEventId { get; set; }

    /// <summary>
    /// The NUC's wall-clock at the moment this response was built. Lets the Field app
    /// compute the per-gate clock offset (NUC vs tablet) for checkpoint verification
    /// and downstream time-window event attribution. Especially important for headless
    /// checkpoint NUCs where reads carry only tag + readTime — the trainer laptop uses
    /// this to detect drift before reconciling reads into events.
    /// </summary>
    public DateTimeOffset ServerTime { get; set; }
}

public sealed class ReceivedSyncDataDto
{
    public Guid Id { get; set; }
    public string ClientRecordId { get; set; } = default!;
    public string SourceDeviceCode { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public object? Payload { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class RaceStartTimeDto
{
    public Guid HeatId { get; set; }
    public int HeatNumber { get; set; }
    public DateTimeOffset GunStartTime { get; set; }
    public Guid SourceDeviceId { get; set; }

    /// <summary>
    /// The event (TestEvent.EventId) this heat belongs to. Null for legacy rows
    /// created before event scoping. Lets the puller (and APTM Main) reconcile
    /// finish results per event at end of test.
    /// </summary>
    public int? EventId { get; set; }

    /// <summary>
    /// The heat's candidate roster. Lets the field app attribute a checkpoint read to a heat by
    /// membership (drift-free, and works for DNF candidates) instead of by gun-time windows. Empty
    /// for legacy rows. Additive — existing consumers ignore it.
    /// </summary>
    public Guid[] CandidateIds { get; set; } = [];
}

/// <summary>
/// Pull/push payload shape for heat-completion records. Used to replicate completion
/// from the finish gate (where it's computed) to the start gate (which displays the
/// frozen timer) via the existing /gate/sync/push channel.
/// </summary>
public sealed class HeatCompletionDto
{
    public Guid HeatId { get; set; }
    public int HeatNumber { get; set; }
    public int ExpectedCount { get; set; }
    public int FinishedCount { get; set; }
    public Guid? LastCandidateId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string ClosureReason { get; set; } = "auto";
    public string SourceDeviceCode { get; set; } = default!;

    /// <summary>Authoritative total heat time (seconds) computed at the finish gate. Carried through
    /// the relay so the start gate stores it and both LEDs freeze on the same value. Null = unknown.</summary>
    public double? DurationSeconds { get; set; }
}

/// <summary>
/// Response for <c>GET /gate/heat/completions</c> — the lightweight completions feed an HHT polls
/// from the finish gate to relay heat completions (with authoritative <see cref="HeatCompletionDto.DurationSeconds"/>)
/// onward to the start gate. <see cref="HighWaterMs"/> is the max <c>received_at</c> of the returned
/// rows; the poller passes it back as <c>sinceMs</c> next time to fetch only newer completions.
/// </summary>
public sealed class HeatCompletionFeedResponse
{
    public List<HeatCompletionDto> Completions { get; set; } = [];
    public long HighWaterMs { get; set; }
}
