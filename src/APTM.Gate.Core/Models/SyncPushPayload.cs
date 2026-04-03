using System.Text.Json;

namespace APTM.Gate.Core.Models;

public sealed class SyncPushPayload
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public string ClientRecordId { get; set; } = default!;
    public JsonElement Payload { get; set; }
}
