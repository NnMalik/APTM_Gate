namespace APTM.Gate.Infrastructure.Entities;

public class RaceStartTime
{
    public Guid Id { get; set; }
    public Guid HeatId { get; set; }
    public int HeatNumber { get; set; }
    public DateTimeOffset GunStartTime { get; set; }
    public DateTimeOffset OriginalGunStartTime { get; set; }
    public Guid SourceDeviceId { get; set; }
    public Guid[] CandidateIds { get; set; } = [];
    public int SourceClockOffsetMs { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
