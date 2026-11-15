using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LineJobs.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LineJobs.Security;

internal sealed class ClientAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ClientBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: >= 32 and <= 512 } key)
            return Task.FromResult(AuthenticateResult.Fail("Invalid client credential."));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string? owner = null;
        foreach (var client in settings.Clients)
            if (CryptographicOperations.FixedTimeEquals(hash, client.Value))
                owner = client.Key;

        return Task.FromResult(owner is null
            ? AuthenticateResult.Fail("Invalid client credential.")
            : AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], SchemeName)),
                SchemeName)));
    }
}
