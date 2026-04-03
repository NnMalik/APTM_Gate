using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;

namespace APTM.Gate.Api.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/sync")
            .RequireAuthorization()
            .WithTags("Sync");

        group.MapPost("/push", async (SyncPushPayload payload, ISyncHubService syncHub, CancellationToken ct) =>
        {
            var result = await syncHub.PushAsync(payload, ct);
            return Results.Ok(new
            {
                accepted = result.Accepted,
                reason = result.Reason,
                clientRecordId = result.ClientRecordId
            });
        })
        .WithName("SyncPush")
        .WithSummary("Push sync data to this gate")
        .WithDescription("Receives sync data from another device. Deduplicates by clientRecordId.");

        group.MapGet("/pull", async (Guid? deviceId, string? deviceCode, long? since,
            ISyncHubService syncHub, CancellationToken ct) =>
        {
            var response = await syncHub.PullAsync(
                deviceId ?? Guid.Empty,
                deviceCode ?? "unknown",
                since ?? 0,
                ct);
            return Results.Ok(response);
        })
        .WithName("SyncPull")
        .WithSummary("Pull data from this gate")
        .WithDescription("Returns processed events, received sync data, and race start times since the given marker.");

        group.MapGet("/status", async (ISyncHubService syncHub, CancellationToken ct) =>
        {
            var status = await syncHub.GetStatusAsync(ct);
            return Results.Ok(status);
        })
        .WithName("SyncStatus")
        .WithSummary("Get sync hub status");
    }
}
