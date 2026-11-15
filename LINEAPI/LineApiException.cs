using System.Net;

namespace LINEAPI;

public enum LineFailure
{
    Http,
    Network,
    Rpc,
    Protocol
}

public sealed class LineApiException : Exception
{
    internal LineApiException(string operation, LineFailure failure, HttpStatusCode? status = null, int? rpcCode = null)
        : base($"LINE {operation} failed ({failure}).")
    {
        Operation = operation;
        Failure = failure;
        HttpStatus = status;
        RpcCode = rpcCode;
    }

    public string Operation { get; }
    public LineFailure Failure { get; }
    public HttpStatusCode? HttpStatus { get; }
    public int? RpcCode { get; }
}
