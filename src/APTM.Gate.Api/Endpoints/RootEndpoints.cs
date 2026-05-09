using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Role-aware static-file routing for the gate's display. <c>GET /</c> serves the right
/// HTML based on the provisioned role:
/// <list type="bullet">
/// <item>Start    → led-start-display.html</item>
/// <item>Finish   → finish-display.html</item>
/// <item>Checkpoint → 204 No Content (headless gate, no screen)</item>
/// <item>un-provisioned → 503 with a hint</item>
/// </list>
/// Direct paths (e.g. /finish-display.html) still work via UseStaticFiles for debugging.
/// </summary>
public static class RootEndpoints
{
    public static void MapRootEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (IGateIdentityProvider identityProvider, IWebHostEnvironment env) =>
        {
            var identity = identityProvider.Current;
            if (identity is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Gate not provisioned",
                    detail: "PUT /gate/identity to set the gate role, then restart the service.");
            }

            if (!Enum.TryParse<GateRole>(identity.Role, out var role))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Invalid identity role",
                    detail: $"Stored role '{identity.Role}' is not a recognised GateRole.");
            }

            return role switch
            {
                GateRole.Start  => ServeFile(env, "led-start-display.html"),
                GateRole.Finish => ServeFile(env, "finish-display.html"),
                GateRole.Checkpoint => Results.NoContent(),
                _ => Results.NotFound()
            };
        })
        .WithTags("Display")
        .WithName("GetRoleDisplay")
        .WithSummary("Role-aware display routing")
        .WithDescription("Serves the appropriate display HTML for this gate's provisioned role. Anonymous (LAN-only).");
    }

    private static IResult ServeFile(IWebHostEnvironment env, string fileName)
    {
        var path = Path.Combine(env.WebRootPath, fileName);
        if (!File.Exists(path))
            return Results.NotFound(new { error = $"Display file '{fileName}' not found in wwwroot." });

        return Results.File(path, "text/html");
    }
}
