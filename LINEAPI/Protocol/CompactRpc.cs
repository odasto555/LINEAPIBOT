using Thrift;
using Thrift.Protocol;
using Thrift.Protocol.Entities;
using Thrift.Transport.Client;

namespace LINEAPI.Protocol;

internal static class CompactRpc
{
    internal const int MaxMessageBytes = 4 * 1024 * 1024;

    internal static async Task<byte[]> EncodeAsync(string operation, WireStruct args, int sequenceId, CancellationToken ct)
    {
        using var transport = new TMemoryBufferTransport(new TConfiguration());
        using var protocol = new TCompactProtocol(transport);
        await protocol.WriteMessageBeginAsync(new TMessage(operation, TMessageType.Call, sequenceId), ct);
        await WriteStructAsync(protocol, args, ct);
        await protocol.WriteMessageEndAsync(ct);
        return transport.GetBuffer();
    }

    internal static async Task<object?> DecodeAsync(string operation, byte[] data, int sequenceId, bool voidResult, CancellationToken ct)
    {
        using var transport = new TMemoryBufferTransport(data, new TConfiguration { MaxMessageSize = MaxMessageBytes });
        using var protocol = new TCompactProtocol(transport);
        var message = await protocol.ReadMessageBeginAsync(ct);
        if (!string.Equals(message.Name, operation, StringComparison.Ordinal) || message.SeqID != sequenceId)
            throw new LineApiException(operation, LineFailure.Protocol);

        var result = await new CompactReader(protocol, ct).ReadStructAsync();
        await protocol.ReadMessageEndAsync(ct);
        if (message.Type == TMessageType.Exception)
            throw new LineApiException(operation, LineFailure.Rpc, rpcCode: result.Get(2) as int?);
        if (message.Type != TMessageType.Reply)
            throw new LineApiException(operation, LineFailure.Protocol);

        var error = result.Fields.FirstOrDefault(field => field.Key > 0 && field.Value is WireStruct);
        if (error.Value is WireStruct detail)
            throw new LineApiException(operation, LineFailure.Rpc, rpcCode: detail.Get(1) as int?);
        if (result.Get(0) is { } value)
            return value;
        if (voidResult && result.Count == 0)
            return null;
        throw new LineApiException(operation, LineFailure.Protocol);
    }

    private static async Task WriteStructAsync(TProtocol protocol, WireStruct value, CancellationToken ct)
    {
        await protocol.WriteStructBeginAsync(new TStruct(), ct);
        foreach (var (id, field) in value.Fields.OrderBy(pair => pair.Key))
        {
            await protocol.WriteFieldBeginAsync(new TField(string.Empty, TypeOf(field), id), ct);
            await WriteValueAsync(protocol, field, ct);
            await protocol.WriteFieldEndAsync(ct);
        }
        await protocol.WriteFieldStopAsync(ct);
        await protocol.WriteStructEndAsync(ct);
    }

    private static TType TypeOf(object value) => value switch
    {
        string => TType.String,
        int => TType.I32,
        WireStruct => TType.Struct,
        WireCollection collection => collection.Type,
        IReadOnlyDictionary<string, string> => TType.Map,
        _ => throw new InvalidOperationException("Unknown field type.")
    };

    private static async Task WriteValueAsync(TProtocol protocol, object value, CancellationToken ct)
    {
        switch (value)
        {
            case string text:
                await protocol.WriteStringAsync(text, ct);
                break;
            case int number:
                await protocol.WriteI32Async(number, ct);
                break;
            case WireStruct fields:
                await WriteStructAsync(protocol, fields, ct);
                break;
            case WireCollection collection:
                if (collection.Type == TType.Set)
                    await protocol.WriteSetBeginAsync(new TSet(collection.ElementType, collection.Items.Count), ct);
                else
                    await protocol.WriteListBeginAsync(new TList(collection.ElementType, collection.Items.Count), ct);
                foreach (var item in collection.Items)
                    await WriteValueAsync(protocol, item, ct);
                if (collection.Type == TType.Set)
                    await protocol.WriteSetEndAsync(ct);
                else
                    await protocol.WriteListEndAsync(ct);
                break;
            case IReadOnlyDictionary<string, string> map:
                await protocol.WriteMapBeginAsync(new TMap(TType.String, TType.String, map.Count), ct);
                foreach (var (key, text) in map)
                {
                    await protocol.WriteStringAsync(key, ct);
                    await protocol.WriteStringAsync(text, ct);
                }
                await protocol.WriteMapEndAsync(ct);
                break;
            default:
                throw new InvalidOperationException("Unknown field type.");
        }
    }
}
