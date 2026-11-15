using LINEAPI.Protocol;
using LINEAPI.Transport;

namespace LINEAPI.Profiles;

public sealed record LineProfile(string UserId, string? DisplayName);

public sealed class ProfileClient
{
    private readonly LineRpcClient _rpc;

    internal ProfileClient(LineRpcClient rpc) => _rpc = rpc;

    public async Task<LineProfile> GetAsync(LineRequestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string operation = "getProfile";
        var result = WireStruct.From(await _rpc.CallAsync("/S4", operation, context.RequireAccount(),
            new WireStruct((1, 1)), cancellationToken), operation);
        return new LineProfile(result.Require<string>(1, operation), result.Get(20) as string);
    }
}
