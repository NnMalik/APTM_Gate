using System.Text.Json;

namespace APTM.Gate.Infrastructure.Entities;

public class ReceivedSyncData
{
    public Guid Id { get; set; }
    public string ClientRecordId { get; set; } = default!;
    public Guid SourceDeviceId { get; set; }
    public string SourceDeviceCode { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public JsonDocument Payload { get; set; } = default!;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
