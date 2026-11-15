namespace LineJobs.Jobs;

internal static class RequestValidation
{
    internal static bool HasText(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    internal static bool IsE164(string? value) =>
        value is { Length: >= 8 and <= 16 } && value[0] == '+' && value[1] is >= '1' and <= '9' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789") < 0;

    internal static bool IsContact(string? value) =>
        IsE164(value) || value is { Length: >= 7 and <= 15 } && value.AsSpan().IndexOfAnyExcept("0123456789") < 0;

    internal static bool IsCountry(string? value) => value is { Length: 2 } && value.All(char.IsAsciiLetter);

    internal static bool TryUserId(string? raw, out string userId)
    {
        userId = raw?.TrimStart('!') ?? string.Empty;
        if (userId.Length != 33 || userId[0] != 'u' || userId.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            return false;
        userId = userId.ToLowerInvariant();
        return true;
    }
}
