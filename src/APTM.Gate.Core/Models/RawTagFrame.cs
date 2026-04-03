namespace APTM.Gate.Core.Models;

public sealed class RawTagFrame
{
    public required string TagEPC { get; init; }
    public int? AntennaPort { get; init; }
    public int? RSSI { get; init; }
    public DateTimeOffset ReadTime { get; init; }
}
