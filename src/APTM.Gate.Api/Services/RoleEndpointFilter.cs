using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Api.Services;

/// <summary>
/// Endpoint filter that rejects requests when the gate's provisioned role is not in the
/// allowed set. Returns 503 if the gate has never been provisioned, 410 Gone if the
/// caller hits an endpoint that is not valid for the current role (e.g. /gate/reader on
/// a Start gate that has no reader). Reads from the cached <see cref="IGateIdentityProvider"/>
/// so each request only does a memory lookup.
/// </summary>
public static class RoleEndpointFilter
{
    /// <summary>Restricts the endpoint group to the given roles.</summary>
    public static TBuilder RequireRoles<TBuilder>(this TBuilder builder, params GateRole[] allowedRoles)
        where TBuilder : IEndpointConventionBuilder
    {
        var allowed = allowedRoles.ToHashSet();

        builder.AddEndpointFilter(async (context, next) =>
        {
            var provider = context.HttpContext.RequestServices.GetRequiredService<IGateIdentityProvider>();
            var identity = provider.Current;

            if (identity is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Gate not provisioned",
                    detail: "PUT /gate/identity to set the gate role, then restart the service.");
            }

            if (!Enum.TryParse<GateRole>(identity.Role, ignoreCase: false, out var role) || !allowed.Contains(role))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status410Gone,
                    title: "Endpoint not valid for this role",
                    detail: $"This endpoint is not available on a {identity.Role} gate. Allowed roles: {string.Join(", ", allowed)}.");
            }

            return await next(context);
        });

        return builder;
    }

    /// <summary>Convenience: roles that have a reader connected (Checkpoint + Finish).</summary>
    public static TBuilder RequireReaderRole<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireRoles(GateRole.Checkpoint, GateRole.Finish);

    /// <summary>Convenience: any provisioned role (rejects un-provisioned NUCs only).</summary>
    public static TBuilder RequireProvisioned<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireRoles(GateRole.Start, GateRole.Checkpoint, GateRole.Finish);
}
