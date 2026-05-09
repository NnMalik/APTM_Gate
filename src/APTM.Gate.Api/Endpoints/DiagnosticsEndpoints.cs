using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Api.Endpoints;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/gate/diagnostics", async (IDiagnosticsService diagnostics, CancellationToken ct) =>
        {
            var result = await diagnostics.GetDiagnosticsAsync(ct);
            return Results.Ok(result);
        })
        .RequireAuthorization()
        .RequireReaderRole()  // Diagnostics is reader-centric — Start gates don't expose it.
        .WithTags("Diagnostics")
        .WithName("GetDiagnostics")
        .WithSummary("Get gate diagnostics")
        .WithDescription("Returns reader status, antenna stats, buffer counts, and DB pool stats.");
    }
}
