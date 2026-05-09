using System.Security.Claims;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;

namespace APTM.Gate.Api.Endpoints;

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate")
            .RequireAuthorization()
            .WithTags("Identity");

        group.MapGet("/identity", async (IGateIdentityService svc, CancellationToken ct) =>
        {
            var info = await svc.GetAsync(ct);
            if (info is null)
                return Results.NotFound(new { error = "Gate has not been provisioned. PUT /gate/identity to set role." });

            return Results.Ok(info);
        })
        .WithName("GetGateIdentity")
        .WithSummary("Get the current gate role + checkpoint sequence")
        .WithDescription("Returns 404 if the gate has never been provisioned by the field app.");

        group.MapPut("/identity", async (
            SetGateIdentityRequest request,
            bool? force,
            HttpContext httpContext,
            IGateIdentityService svc,
            CancellationToken ct) =>
        {
            var setBy = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var result = await svc.SetAsync(request, setBy, force ?? false, ct);

            if (result.Success)
            {
                return Results.Ok(new
                {
                    identity = result.Identity,
                    restartRequired = result.RestartRequired
                });
            }

            if (result.Conflict)
            {
                return Results.Conflict(new
                {
                    error = result.Error,
                    current = result.Identity
                });
            }

            return Results.BadRequest(new { error = result.Error });
        })
        .WithName("SetGateIdentity")
        .WithSummary("Provision the gate role (one-time, restart required)")
        .WithDescription(
            "Sets the gate's predefined role (Start | Checkpoint | Finish) and, for Checkpoint, " +
            "the sequence number. Idempotent for same payload. Returns 409 if the gate is already " +
            "provisioned with a different role unless ?force=true is supplied — force purges " +
            "raw_tag_buffer and processed_events to prevent stale reads bleeding across role semantics. " +
            "When restartRequired=true is returned, the service must be restarted on the NUC for " +
            "worker registration changes to take effect.");
    }
}
