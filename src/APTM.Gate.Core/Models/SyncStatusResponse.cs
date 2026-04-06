namespace APTM.Gate.Core.Models;

public sealed class SyncStatusResponse
{
    public string DeviceCode { get; set; } = default!;
    public string GateRole { get; set; } = default!;
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
