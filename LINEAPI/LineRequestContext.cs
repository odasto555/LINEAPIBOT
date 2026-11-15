namespace LINEAPI;

public sealed class LineRequestContext
{
    public LineRequestContext(string userAgent, string application, string locale, string? accessToken = null)
    {
        UserAgent = Validate(userAgent, nameof(userAgent), false);
        Application = Validate(application, nameof(application), true);
        Locale = Validate(locale, nameof(locale), false);
        AccessToken = accessToken is null ? null : Validate(accessToken, nameof(accessToken), false);
    }

    public string UserAgent { get; }
    public string Application { get; }
    public string Locale { get; }
    internal string? AccessToken { get; }

    internal LineRequestContext RequireAccount()
    {
        if (AccessToken is null)
            throw new ArgumentException("An account access token is required.");
        return this;
    }

    internal LineRequestContext RequireAnonymous()
    {
        if (AccessToken is not null)
            throw new ArgumentException("Registration requires an unauthenticated context.");
        return this;
    }

    private static string Validate(string value, string parameter, bool allowTab)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 ||
            value.Any(c => (c < ' ' && (!allowTab || c != '\t')) || c > '~'))
        {
            throw new ArgumentException("Invalid header value.", parameter);
        }
        return value;
    }
}
