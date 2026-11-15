namespace LINEAPI.Protocol;

internal static class LineIds
{
    internal static string User(string value)
    {
        if (value is null || !TryReadUser(value, out var id))
            throw new ArgumentException("Invalid LINE user ID.", nameof(value));
        return id;
    }

    internal static bool TryReadUser(string value, out string id)
    {
        id = value.StartsWith('!') ? value[1..] : value;
        return IsMid(id, 'u');
    }

    internal static bool IsMid(string value, char kind)
        => value.Length == 33 && value[0] == kind && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
