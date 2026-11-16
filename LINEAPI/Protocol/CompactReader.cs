using Thrift.Protocol;
using Thrift.Protocol.Entities;

namespace LINEAPI.Protocol;

internal sealed class CompactReader(TProtocol protocol, CancellationToken ct, CompactHexPreview? preview = null)
{
    private int _remaining = 65_536;

    internal async Task<WireStruct> ReadStructAsync(int depth = 0)
    {
        await protocol.ReadStructBeginAsync(ct);
        var result = new WireStruct();
        while (true)
        {
            var field = await protocol.ReadFieldBeginAsync(ct);
            if (field.Type == TType.Stop)
                break;
            if (!result.TryAdd(field.ID, await ReadValueAsync(field.Type, depth + 1)))
                throw new InvalidDataException();
            await protocol.ReadFieldEndAsync(ct);
        }
        await protocol.ReadStructEndAsync(ct);
        return result;
    }

    private async Task<object> ReadValueAsync(TType type, int depth)
    {
        if (depth > 24 || --_remaining < 0)
            throw new InvalidDataException();
        return type switch
        {
            TType.Bool => await protocol.ReadBoolAsync(ct),
            TType.Byte => await protocol.ReadByteAsync(ct),
            TType.I16 => await protocol.ReadI16Async(ct),
            TType.I32 => await protocol.ReadI32Async(ct),
            TType.I64 => await protocol.ReadI64Async(ct),
            TType.Double => await protocol.ReadDoubleAsync(ct),
            TType.String => await ReadStringAsync(),
            TType.Struct => await ReadStructAsync(depth),
            TType.List or TType.Set => await ReadCollectionAsync(type, depth),
            TType.Map => await ReadMapAsync(depth),
            _ => throw new InvalidDataException()
        };
    }

    private async Task<string> ReadStringAsync()
    {
        if (preview is null)
            return await protocol.ReadStringAsync(ct);

        var bytes = await protocol.ReadBinaryAsync(ct);
        preview.MaskBinary(bytes.Length);
        return string.Empty;
    }

    private async Task<WireCollection> ReadCollectionAsync(TType type, int depth)
    {
        var (element, count) = type == TType.Set
            ? AsCollection(await protocol.ReadSetBeginAsync(ct))
            : AsCollection(await protocol.ReadListBeginAsync(ct));
        var items = new object[CheckCount(count)];
        for (var i = 0; i < count; i++)
            items[i] = await ReadValueAsync(element, depth + 1);
        if (type == TType.Set)
            await protocol.ReadSetEndAsync(ct);
        else
            await protocol.ReadListEndAsync(ct);
        return new WireCollection(type, element, items);
    }

    private async Task<WireCollection> ReadMapAsync(int depth)
    {
        var map = await protocol.ReadMapBeginAsync(ct);
        var entries = new object[CheckCount(checked(map.Count * 2))];
        for (var i = 0; i < entries.Length; i += 2)
        {
            entries[i] = await ReadValueAsync(map.KeyType, depth + 1);
            entries[i + 1] = await ReadValueAsync(map.ValueType, depth + 1);
        }
        await protocol.ReadMapEndAsync(ct);
        return new WireCollection(TType.Map, TType.Stop, entries);
    }

    private int CheckCount(int count) => count >= 0 && count <= _remaining ? count : throw new InvalidDataException();
    private static (TType, int) AsCollection(TSet set) => (set.ElementType, set.Count);
    private static (TType, int) AsCollection(TList list) => (list.ElementType, list.Count);
}
