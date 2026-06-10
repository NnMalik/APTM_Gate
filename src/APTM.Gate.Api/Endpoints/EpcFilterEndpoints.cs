using System.Security.Claims;
using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// CRUD-style endpoints for the per-NUC EPC range filter. When enabled, the gate only
/// ingests tag reads whose EPC falls within the configured [start, end] range — every
/// other read is dropped at ingestion (<c>TagBufferService.InsertRawTagsAsync</c>).
/// Backed by the singleton <c>epc_filter</c> table; when no row exists the filter is
/// disabled (every read is accepted).
///
/// A successful PUT invalidates the in-memory <see cref="IEpcFilterProvider"/> cache, so
/// the new range takes effect on the next ingest batch — no service restart required.
/// </summary>
public static class EpcFilterEndpoints
{
    public static void MapEpcFilterEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/gate/epc-filter")
            .RequireAuthorization()
            .RequireReaderRole()  // Start gates have no reader / no ingestion.
            .WithTags("Reader");

        group.MapGet("/", async (IEpcFilterService svc, CancellationToken ct) =>
        {
            var info = await svc.GetAsync(ct);
            return Results.Ok(info);
        })
        .WithName("GetEpcRangeFilter")
        .WithSummary("Get the active EPC range filter")
        .WithDescription("Returns the enabled flag + range bounds plus a 'source' flag — 'db' if a row exists, 'default' if the filter has never been configured.");

        group.MapPut("/", async (
            UpdateEpcFilterRequest request,
            HttpContext httpContext,
            IEpcFilterService svc,
            CancellationToken ct) =>
        {
            var updatedBy = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var result = await svc.SetAsync(request, updatedBy, ct);

            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(new
            {
                filter = result.Filter,
                message = result.Filter!.Enabled
                    ? $"EPC filter enabled for range {result.Filter.RangeStart}–{result.Filter.RangeEnd}."
                    : "EPC filter disabled — all reads accepted."
            });
        })
        .WithName("SetEpcRangeFilter")
        .WithSummary("Update the EPC range filter")
        .WithDescription(
            "Upserts the singleton epc_filter row. When 'enabled' is true, 'rangeStart' and " +
            "'rangeEnd' are required, must be valid hexadecimal EPC values, and start must be " +
            "<= end. The change takes effect on the next ingest batch — no restart required.");
    }
}
