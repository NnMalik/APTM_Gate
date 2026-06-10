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

    /// <summary>
    /// The gate's active event (TestEvent.EventId) at the moment this read was
    /// ingested. Lets the buffer processor attribute the read — and compute elapsed
    /// time against the correct event's gun — even if the active event is switched
    /// before the read is processed. Null when no config was loaded at read time
    /// (the read is still stored) or on checkpoint gates (no event awareness).
    /// </summary>
    public int? EventId { get; set; }
}
