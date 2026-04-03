using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

public interface ISyncHubService
{
    Task<SyncPushResult> PushAsync(SyncPushPayload payload, CancellationToken ct = default);
    Task<SyncPullResponse> PullAsync(Guid pullerDeviceId, string pullerDeviceCode, long sinceEventId, CancellationToken ct = default);
    Task<SyncStatusResponse> GetStatusAsync(CancellationToken ct = default);
}
