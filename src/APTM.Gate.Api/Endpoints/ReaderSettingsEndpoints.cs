using System.Security.Claims;
using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// CRUD-style endpoints for the per-NUC UHF reader connection settings (host, port,
/// power, filter, reconnect delay). Backed by the singleton <c>reader_config</c> table
/// with a fallback to <c>Reader:*</c> in appsettings — so a brand-new NUC works on its
/// configured defaults until the field app overrides them.
///
/// A successful PUT bumps the in-memory provider cache and asks the reader worker to
/// disconnect; the worker then reconnects within ~5s with the new values, no service
/// restart required.
/// </summary>
public static class ReaderSettingsEndpoints
{
    public static void MapReaderSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/gate/reader/settings")
            .RequireAuthorization()
            .RequireReaderRole()  // Start gates have no reader.
            .WithTags("Reader");

        group.MapGet("/", async (IReaderConfigService svc, CancellationToken ct) =>
        {
            var info = await svc.GetAsync(ct);
            return Results.Ok(info);
        })
        .WithName("GetReaderSettings")
        .WithSummary("Get the active UHF reader connection settings")
        .WithDescription("Returns host/port/power/filter/reconnectDelay plus a 'source' flag — 'db' if an override row exists, 'config' if falling back to appsettings.");

        group.MapPut("/", async (
            UpdateReaderSettingsRequest request,
            HttpContext httpContext,
            IReaderConfigService svc,
            IReaderStatusProvider reader,
            CancellationToken ct) =>
        {
            var updatedBy = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var result = await svc.SetAsync(request, updatedBy, ct);

            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            // Best-effort reconnect — drop the existing TCP connection so the worker's
            // reconnect loop picks up the new host/port within one cycle (~5s).
            // Failure here is non-fatal; the change is already persisted and will be
            // applied at the next natural reconnect.
            var reconnectTriggered = false;
            try
            {
                await reader.DisconnectReaderAsync(ct);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    try { await reader.ReconnectReaderAsync(CancellationToken.None); }
                    catch { /* best-effort */ }
                }, CancellationToken.None);
                reconnectTriggered = true;
            }
            catch { /* best-effort */ }

            return Results.Ok(new
            {
                settings = result.Settings,
                reconnectTriggered,
                message = reconnectTriggered
                    ? "Settings saved. Reader reconnecting with new configuration..."
                    : "Settings saved. Will be applied on the next reader reconnect."
            });
        })
        .WithName("SetReaderSettings")
        .WithSummary("Update the active UHF reader connection settings")
        .WithDescription(
            "Upserts the singleton reader_config row. Host + Port are required; other fields " +
            "are optional and preserve their previous value when omitted. On success, the worker " +
            "is asked to drop its current connection and reconnect — picking up the new values " +
            "within ~5s. No service restart required.");
    }
}
