using Thrift.Protocol.Entities;

namespace LINEAPI.Protocol;

internal sealed class WireStruct(params (short Id, object Value)[] values)
{
    private readonly Dictionary<short, object> _fields = values.ToDictionary(x => x.Id, x => x.Value);
    internal IEnumerable<KeyValuePair<short, object>> Fields => _fields;
    internal int Count => _fields.Count;
    internal bool TryAdd(short id, object value) => _fields.TryAdd(id, value);
    internal object? Get(short id) => _fields.GetValueOrDefault(id);
    internal T Require<T>(short id, string operation) => Get(id) is T value
        ? value
        : throw new LineApiException(operation, LineFailure.Protocol);
    internal static WireStruct From(object? value, string operation) => value as WireStruct
        ?? throw new LineApiException(operation, LineFailure.Protocol);
}

internal sealed record WireCollection(TType Type, TType ElementType, IReadOnlyList<object> Items);
