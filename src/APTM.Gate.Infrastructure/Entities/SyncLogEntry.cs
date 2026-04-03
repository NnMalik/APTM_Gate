namespace APTM.Gate.Infrastructure.Entities;

public class SyncLogEntry
{
    public Guid Id { get; set; }
    public Guid PullerDeviceId { get; set; }
    public string PullerDeviceCode { get; set; } = default!;
    public long LastProcessedEventId { get; set; }
    public Guid? LastReceivedSyncId { get; set; }
    public DateTimeOffset PulledAt { get; set; } = DateTimeOffset.UtcNow;
}
