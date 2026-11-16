namespace LINEAPI;

public sealed record LineRpcTrace(
    DateTime Timestamp, string Operation, string Phase, string? Host = null, string? Path = null,
    int? HttpStatus = null, long? ElapsedMilliseconds = null, int? RpcCode = null, string? Outcome = null);
