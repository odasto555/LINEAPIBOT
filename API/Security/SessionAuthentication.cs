using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LineJobs.Security;

internal sealed class SessionAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, UserSessions sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "LineSession";
    internal const string SessionClaim = "linejobs_session_hash";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count == 0)
            return AuthenticateResult.NoResult();

        if (authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: 43 } token ||
            !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return AuthenticateResult.Fail("Invalid session token.");

        var hash = UserSessions.HashToken(token);
        var user = await sessions.FindAuthenticatedUserAsync(hash, Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Invalid or expired session.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Id),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(SessionClaim, hash)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }
}
