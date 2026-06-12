namespace APTM.Gate.Core.Models;

public sealed class SyncStatusResponse
{
    public string DeviceCode { get; set; } = default!;

    /// <summary>The provisioned role from gate_identity. Source of truth.</summary>
    public string GateRole { get; set; } = default!;

    /// <summary>
    /// The role recorded in the active gate_config row at last config-push. Compared with
    /// <see cref="GateRole"/> to detect a desync (e.g. identity force-flipped after a
    /// config push). Null when no config has been pushed.
    /// </summary>
    public string? ConfiguredRole { get; set; }

    public int? ActiveEventId { get; set; }
    public string? ActiveEventName { get; set; }
    public Guid TestInstanceId { get; set; }
    public int ProcessedEventCount { get; set; }
    public int ReceivedSyncDataCount { get; set; }
    public int RaceStartTimesCount { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public List<SyncPullInfo> SyncPulls { get; set; } = [];

    /// <summary>Highest processed_events.id on the gate (0 when empty).</summary>
    public long MaxEventId { get; set; }

    /// <summary>
    /// Processed events no real device has pulled yet (WIPE:/ERASE: audit markers
    /// excluded). The number the operator must see reach 0 before erase/power-off.
    /// </summary>
    public long UnpulledEventCount { get; set; }

    /// <summary>Raw reads captured but not yet run through the dedup processor.</summary>
    public int PendingRawCount { get; set; }

    /// <summary>
    /// Reads parsed off the wire but not yet inserted into raw_tag_buffer (in-memory).
    /// Normally 0; non-zero means the DB consumer is catching up or retrying.
    /// </summary>
    public int IngestQueueDepth { get; set; }
}

public sealed class SyncPullInfo
{
    public string DeviceCode { get; set; } = default!;
    public DateTimeOffset LastPulledAt { get; set; }
    public long EventsPulled { get; set; }
}
