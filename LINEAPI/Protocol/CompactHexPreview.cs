namespace LINEAPI.Protocol;

internal static class CompactHexPreview
{
    private const int DisplayLimit = 512;
    private const string Digits = "0123456789ABCDEF";

    internal static string Format(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        var count = Math.Min(data.Length, DisplayLimit);
        var result = new char[count * 3 - 1];
        for (var i = 0; i < count; i++)
        {
            var offset = i * 3;
            if (i > 0)
                result[offset - 1] = ' ';
            result[offset] = Digits[data[i] >> 4];
            result[offset + 1] = Digits[data[i] & 15];
        }

        var hex = new string(result);
        return data.Length > DisplayLimit ? hex + " …" : hex;
    }
}
