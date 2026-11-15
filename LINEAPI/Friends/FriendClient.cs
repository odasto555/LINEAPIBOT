using System.Globalization;
using LINEAPI.Protocol;
using LINEAPI.Transport;
using Thrift.Protocol.Entities;

namespace LINEAPI.Friends;

public sealed class FriendClient
{
    private const string Path = "/RE4";
    private readonly LineRpcClient _rpc;

    internal FriendClient(LineRpcClient rpc) => _rpc = rpc;

    public async Task<IReadOnlyList<string>> GetUserIdsAsync(LineRequestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string operation = "getUserFriendIds";
        var result = await _rpc.CallAsync(Path, operation, context.RequireAccount(),
            new WireStruct((1, new WireStruct((2, 0)))), cancellationToken);
        return ReadUserIds(result, operation);
    }

    public async Task<IReadOnlyList<string>> SyncContactsAsync(
        LineRequestContext context, IReadOnlyList<string> phoneNumbers, int revision, int sequenceId = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(phoneNumbers);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceId);
        if (phoneNumbers.Count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(phoneNumbers));

        var contacts = phoneNumbers.Select((phone, i) => new WireStruct(
            (1, 0),
            (2, (i + 1).ToString(CultureInfo.InvariantCulture)),
            (3, new WireCollection(TType.List, TType.String, new[] { NormalizePhone(phone) })))).ToArray();

        const string operation = "syncContactsV3";
        var args = new WireStruct((1, new WireStruct((1, revision), (2, new WireCollection(TType.List, TType.Struct, contacts)))));
        var result = await _rpc.CallAsync(Path, operation, context.RequireAccount(), args, cancellationToken, sequenceId);
        return ReadUserIds(result, operation);
    }

    private static string NormalizePhone(string phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        if (phone.Length > 32 || phone.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or ' ' or '-' or '(' or ')')))
            throw new ArgumentException("Invalid contact phone number.", nameof(phone));

        var digits = string.Concat(phone.Where(char.IsAsciiDigit));
        if (digits.Length is < 7 or > 15)
            throw new ArgumentException("Invalid contact phone number length.", nameof(phone));
        return digits;
    }

    private static IReadOnlyList<string> ReadUserIds(object? value, string operation)
    {
        if (value is not (WireStruct or WireCollection))
            throw new LineApiException(operation, LineFailure.Protocol);

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(value, ids, seen);
        return ids.AsReadOnly();
    }

    private static void Collect(object value, List<string> ids, HashSet<string> seen)
    {
        switch (value)
        {
            case string text when LineIds.TryReadUser(text, out var id) && seen.Add(id):
                ids.Add(id);
                break;
            case WireStruct fields:
                foreach (var field in fields.Fields)
                    Collect(field.Value, ids, seen);
                break;
            case WireCollection collection:
                foreach (var item in collection.Items)
                    Collect(item, ids, seen);
                break;
        }
    }
}
