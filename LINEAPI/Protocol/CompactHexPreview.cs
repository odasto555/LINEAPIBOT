using Thrift;
using Thrift.Protocol;
using Thrift.Protocol.Entities;
using Thrift.Transport.Client;

namespace LINEAPI.Protocol;

internal sealed class CompactHexPreview(TMemoryBufferTransport transport, int length)
{
    private const int DisplayLimit = 512;
    private const int ScanLimit = 16 * 1024;
    private const string Digits = "0123456789ABCDEF";
    private readonly bool[] _masked = new bool[Math.Min(length, DisplayLimit)];

    internal static async Task<string?> TryFormatAsync(
        string operation, int sequenceId, byte[] data, bool request, CancellationToken ct)
    {
        if (data.Length is 0 or > ScanLimit || data[0] != 0x82)
            return null;

        using var transport = new TMemoryBufferTransport(data, new TConfiguration { MaxMessageSize = ScanLimit });
        using var protocol = new TCompactProtocol(transport);
        try
        {
            var message = await protocol.ReadMessageBeginAsync(ct);
            if (message.Name != operation || message.SeqID != sequenceId ||
                (request ? message.Type != TMessageType.Call : message.Type is not (TMessageType.Reply or TMessageType.Exception)))
                return null;

            var preview = new CompactHexPreview(transport, data.Length);
            await new CompactReader(protocol, ct, preview).ReadStructAsync();
            await protocol.ReadMessageEndAsync(ct);
            return transport.Position == data.Length ? preview.Format(data) : null;
        }
        catch (Exception ex) when (ex is TException or InvalidDataException or IOException or
            OverflowException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }

    internal void MaskBinary(int byteCount)
    {
        var end = Math.Min(transport.Position, _masked.Length);
        var start = transport.Position - byteCount;
        if (start < end)
            Array.Fill(_masked, true, start, end - start);
    }

    private string Format(byte[] data)
    {
        var result = new char[_masked.Length * 3 - 1];
        for (var i = 0; i < _masked.Length; i++)
        {
            var offset = i * 3;
            if (i > 0)
                result[offset - 1] = ' ';
            result[offset] = _masked[i] ? '*' : Digits[data[i] >> 4];
            result[offset + 1] = _masked[i] ? '*' : Digits[data[i] & 15];
        }

        var hex = new string(result);
        return data.Length > DisplayLimit ? hex + " …" : hex;
    }
}
