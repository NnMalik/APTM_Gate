namespace APTM.Gate.Core.Models;

public sealed class SyncPullResponse
{
    public List<ProcessedEventDto> ProcessedEvents { get; set; } = [];
    public List<ReceivedSyncDataDto> ReceivedSyncData { get; set; } = [];
    public List<RaceStartTimeDto> RaceStartTimes { get; set; } = [];
    public long HighWaterMark { get; set; }
    public long? SyncDataHighWaterMs { get; set; }
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
}
