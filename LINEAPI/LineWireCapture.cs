namespace LINEAPI;

public sealed record LineWireCapture(
    DateTime Timestamp, string Operation, string Direction, string StartLine, string Headers, byte[]? Body);
