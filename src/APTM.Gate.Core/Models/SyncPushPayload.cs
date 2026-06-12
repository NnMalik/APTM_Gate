using System.Text.Json;

namespace APTM.Gate.Core.Models;

public sealed class SyncPushPayload
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public string ClientRecordId { get; set; } = default!;
    public JsonElement Payload { get; set; }

    /// <summary>
    /// Sender-to-gate clock offset (gateClock − senderClock, ms) measured by the
    /// sender NTP-style against GET /gate/time shortly before this push. When present,
    /// timestamps inside Payload can be converted to the gate's clock exactly,
    /// regardless of how late the push arrives (queued/retried/offline). Null when the
    /// sender is an older HHT that doesn't measure — receivers fall back to
    /// receipt-time heuristics.
    /// </summary>
    public long? GateClockOffsetMs { get; set; }

    /// <summary>Round-trip of the offset sample (ms) — bounds its uncertainty.</summary>
    public long? GateClockOffsetRttMs { get; set; }
}
