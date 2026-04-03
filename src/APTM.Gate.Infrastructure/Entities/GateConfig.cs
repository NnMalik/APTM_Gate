namespace APTM.Gate.Infrastructure.Entities;

public class GateConfig
{
    public int Id { get; set; }
    public Guid TestInstanceId { get; set; }
    public string TestInstanceName { get; set; } = default!;
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
    public string GateRole { get; set; } = default!;
    public int? CheckpointSequence { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public int DataSnapshotVersion { get; set; }
    public int ClockOffsetMs { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
