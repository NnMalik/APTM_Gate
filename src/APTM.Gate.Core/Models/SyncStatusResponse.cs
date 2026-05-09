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
}

public sealed class SyncPullInfo
{
    public string DeviceCode { get; set; } = default!;
    public DateTimeOffset LastPulledAt { get; set; }
    public long EventsPulled { get; set; }
}
