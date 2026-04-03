namespace APTM.Gate.Core.Models;

public sealed class SyncPushResult
{
    public bool Accepted { get; init; }
    public string? Reason { get; init; }
    public string ClientRecordId { get; init; } = default!;

    public static SyncPushResult Ok(string clientRecordId) =>
        new() { Accepted = true, ClientRecordId = clientRecordId };

    public static SyncPushResult Duplicate(string clientRecordId) =>
        new() { Accepted = false, Reason = "duplicate", ClientRecordId = clientRecordId };
}
