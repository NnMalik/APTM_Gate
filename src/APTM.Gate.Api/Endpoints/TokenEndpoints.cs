using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

public static class TokenEndpoints
{
    public static void MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/tokens")
            .RequireAuthorization()
            .WithTags("Tokens");

        group.MapGet("/", async (GateDbContext db, CancellationToken ct) =>
        {
            var tokens = await db.AcceptedTokens
                .AsNoTracking()
                .OrderBy(t => t.CreatedAt)
                .Select(t => new
                {
                    t.Id,
                    t.Token,
                    t.Label,
                    CreatedAt = t.CreatedAt.ToString("o")
                })
                .ToListAsync(ct);

            return Results.Ok(tokens);
        })
        .WithName("ListTokens")
        .WithSummary("List all registered device tokens");

        group.MapPost("/", async (AddTokenRequest req, GateDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Label))
                return Results.BadRequest(new { message = "Token and label are required." });

            // Dedup by token (case-insensitive)
            var exists = await db.AcceptedTokens
                .AnyAsync(t => t.Token.ToLower() == req.Token.ToLower(), ct);

            if (exists)
                return Results.Conflict(new { message = "Token already registered." });

            var entity = new AcceptedTokenEntity
            {
                Id = Guid.NewGuid(),
                Token = req.Token,
                Label = req.Label,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.AcceptedTokens.Add(entity);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { entity.Id, entity.Token });
        })
        .WithName("AddToken")
        .WithSummary("Register a new device token");

        group.MapDelete("/{token}", async (string token, GateDbContext db, CancellationToken ct) =>
        {
            var entity = await db.AcceptedTokens
                .FirstOrDefaultAsync(t => t.Token.ToLower() == token.ToLower(), ct);

            if (entity is null)
                return Results.NotFound(new { message = "Token not found." });

            db.AcceptedTokens.Remove(entity);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { message = "Token removed." });
        })
        .WithName("DeleteToken")
        .WithSummary("Remove a registered device token");
    }
}

public sealed class AddTokenRequest
{
    public string Token { get; set; } = default!;
    public string Label { get; set; } = default!;
}
