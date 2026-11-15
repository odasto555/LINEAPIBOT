using LINEAPI.Protocol;
using LINEAPI.Transport;
using Thrift.Protocol.Entities;

namespace LINEAPI.Groups;

public sealed record CreatedGroup(string GroupId);

public sealed class GroupClient
{
    private readonly LineRpcClient _rpc;

    internal GroupClient(LineRpcClient rpc) => _rpc = rpc;

    public async Task<CreatedGroup> CreateAsync(
        LineRequestContext context, string name, IReadOnlyList<string> userIds, int revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (name.Length > 1024)
            throw new ArgumentOutOfRangeException(nameof(name));
        if (userIds.Count is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(userIds));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = userIds.Select(LineIds.User).Where(seen.Add).ToArray();

        const string operation = "createChat";
        var args = new WireStruct((1, new WireStruct(
            (1, revision), (2, 1), (3, name), (4, new WireCollection(TType.Set, TType.String, members)), (5, string.Empty))));
        var result = WireStruct.From(await _rpc.CallAsync("/S4", operation, context.RequireAccount(), args, cancellationToken), operation);
        var groupId = result.Require<WireStruct>(1, operation).Require<string>(2, operation);
        if (!LineIds.IsMid(groupId, 'c'))
            throw new LineApiException(operation, LineFailure.Protocol);
        return new CreatedGroup(groupId);
    }
}
