using System.Security.Claims;
using System.Text.Encodings.Web;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APTM.Gate.Api.Services;

public class DeviceTokenAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The master device code from appsettings. Always accepted, can't be deleted via API.
    /// </summary>
    public string DeviceCode { get; set; } = default!;
}

/// <summary>
/// Device token auth — validates X-Device-Token header against:
/// 1. The master token from appsettings (always valid)
/// 2. Dynamic tokens stored in the accepted_tokens DB table
/// Falls back to Authorization: Bearer for backward compatibility.
/// </summary>
public sealed class DeviceTokenAuthHandler : AuthenticationHandler<DeviceTokenAuthOptions>
{
    public const string SchemeName = "DeviceToken";
    private const string DeviceTokenHeader = "X-Device-Token";

    private readonly IServiceScopeFactory _scopeFactory;

    public DeviceTokenAuthHandler(
        IOptionsMonitor<DeviceTokenAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory scopeFactory)
        : base(options, logger, encoder)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Primary: X-Device-Token header
        var token = Request.Headers[DeviceTokenHeader].FirstOrDefault();

        // Fallback: Authorization: Bearer
        if (string.IsNullOrEmpty(token))
        {
            var authHeader = Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = authHeader["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        // 1. Check master token from config (always valid)
        if (string.Equals(token, Options.DeviceCode, StringComparison.OrdinalIgnoreCase))
            return Success(token);

        // 2. Check dynamic tokens from DB
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();
            var exists = await db.AcceptedTokens
                .AnyAsync(t => t.Token.ToLower() == token.ToLower());

            if (exists)
                return Success(token);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check dynamic tokens from DB — falling back to master token only");
        }

        return AuthenticateResult.Fail("Invalid token");
    }

    private AuthenticateResult Success(string token)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, token),
            new Claim(ClaimTypes.Role, "Device")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
