using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace APTM.Gate.Api.Services;

public class DeviceTokenAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The accepted device code. X-Device-Token must match this value (case-insensitive).
    /// </summary>
    public string DeviceCode { get; set; } = default!;
}

/// <summary>
/// Device token auth — validates X-Device-Token header against the configured device code.
/// The Field app sends its provisioned device code (e.g. "Tab-01") as the token.
/// Falls back to Authorization: Bearer for backward compatibility.
/// </summary>
public sealed class DeviceTokenAuthHandler : AuthenticationHandler<DeviceTokenAuthOptions>
{
    public const string SchemeName = "DeviceToken";
    private const string DeviceTokenHeader = "X-Device-Token";

    public DeviceTokenAuthHandler(
        IOptionsMonitor<DeviceTokenAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
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
            return Task.FromResult(AuthenticateResult.NoResult());

        // Simple string comparison against the configured device code
        var isValid = string.Equals(token, Options.DeviceCode, StringComparison.OrdinalIgnoreCase);

        if (!isValid)
            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "gate-device"),
            new Claim(ClaimTypes.Role, "Device")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
