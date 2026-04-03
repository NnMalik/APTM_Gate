namespace APTM.Gate.Infrastructure.Entities;

public class RawTagBuffer
{
    public long Id { get; set; }
    public string TagEPC { get; set; } = default!;
    public DateTimeOffset ReadTime { get; set; }
    public int? AntennaPort { get; set; }
    public decimal? RSSI { get; set; }
    public string Status { get; set; } = "PENDING";
    public bool IsDuplicate { get; set; }
    public DateTimeOffset InsertedAt { get; set; } = DateTimeOffset.UtcNow;
}
