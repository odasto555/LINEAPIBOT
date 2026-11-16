using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using LINEAPI.Protocol;
using Thrift;

namespace LINEAPI.Transport;

internal sealed class LineRpcClient(HttpClient httpClient, Uri baseUri)
{
    internal async Task<object?> CallAsync(
        string path, string operation, LineRequestContext context, WireStruct args,
        CancellationToken cancellationToken, int sequenceId = 1, bool voidResult = false)
    {
        var body = await CompactRpc.EncodeAsync(operation, args, sequenceId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-thrift");
        request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
        request.Headers.TryAddWithoutValidation("X-Line-Application", context.Application);
        request.Headers.TryAddWithoutValidation("X-Lal", context.Locale);
        request.Headers.TryAddWithoutValidation("X-Lpv", "1");
        if (context.AccessToken is { } token)
            request.Headers.TryAddWithoutValidation("X-Line-Access", token);

        var trace = context.Trace;
        var capture = context.Capture;
        var uri = request.RequestUri!;
        if (capture is not null)
        {
            request.Content.Headers.ContentLength = body.Length;
            await capture(new LineWireCapture(DateTime.UtcNow, operation, "REQ",
                $"{request.Method} {uri.AbsoluteUri} HTTP/{request.Version}",
                HeaderLines(request.Headers, request.Content.Headers), body), cancellationToken);
        }

        if (trace is not null)
        {
            await trace(new LineRpcTrace(DateTime.UtcNow, operation, "send", uri.Host, uri.AbsolutePath,
                PayloadHex: CompactHexPreview.Format(body), PayloadBytes: body.Length), cancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        int? status = null;
        byte[]? bytes = null;
        string? responseLine = null;
        var responseHeaders = string.Empty;
        LineApiException? failure = null;
        var decoded = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(httpClient.Timeout);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            status = (int)response.StatusCode;
            if (capture is not null)
            {
                responseLine = $"HTTP/{response.Version} {status} {response.ReasonPhrase}".TrimEnd();
                responseHeaders = HeaderLines(response.Headers, response.Content.Headers);
            }

            if (!response.IsSuccessStatusCode)
            {
                if (capture is not null)
                {
                    bytes = await ReadFailureBodyAsync(response.Content, deadline.Token, cancellationToken);
                    responseHeaders = HeaderLines(response.Headers, response.Content.Headers, response.TrailingHeaders);
                }
                throw new LineApiException(operation, LineFailure.Http, response.StatusCode);
            }

            bytes = await ReadResponseAsync(response.Content, deadline.Token);
            if (capture is not null)
                responseHeaders = HeaderLines(response.Headers, response.Content.Headers, response.TrailingHeaders);
            var result = await CompactRpc.DecodeAsync(operation, bytes, sequenceId, voidResult, deadline.Token);
            decoded = true;
            return result;
        }
        catch (LineApiException ex)
        {
            failure = ex;
            throw;
        }
        catch (Exception ex) when (ex is TException or InvalidDataException or OverflowException)
        {
            throw failure = new LineApiException(operation, LineFailure.Protocol);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw failure = new LineApiException(operation, LineFailure.Network);
        }
        finally
        {
            if (capture is not null && responseLine is not null && !cancellationToken.IsCancellationRequested)
            {
                await capture(new LineWireCapture(DateTime.UtcNow, operation, "RES", responseLine, responseHeaders, bytes),
                    cancellationToken);
            }

            if (trace is not null && !cancellationToken.IsCancellationRequested)
            {
                var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var hex = bytes is null ? null : CompactHexPreview.Format(bytes);
                var outcome = failure?.Failure.ToString() ?? (decoded ? "ok" : "unexpected");
                await trace(new LineRpcTrace(DateTime.UtcNow, operation, "receive", uri.Host, uri.AbsolutePath, status,
                    elapsed, failure?.RpcCode, outcome, hex, bytes?.Length), cancellationToken);
            }
        }
    }

    private static string HeaderLines(params HttpHeaders[] headers) =>
        string.Join(Environment.NewLine, headers.SelectMany(group => group.NonValidated)
            .SelectMany(header => header.Value.Select(value => $"{header.Key}: {value}")));

    private static async Task<byte[]?> ReadFailureBodyAsync(HttpContent content, CancellationToken ct, CancellationToken caller)
    {
        try
        {
            return await ReadResponseAsync(content, ct);
        }
        catch (Exception ex) when (!caller.IsCancellationRequested &&
            ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadResponseAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > CompactRpc.MaxMessageBytes)
            throw new InvalidDataException();

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int size;
        while ((size = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + size > CompactRpc.MaxMessageBytes)
                throw new InvalidDataException();
            buffer.Write(chunk, 0, size);
        }

        if (buffer.Length == 0)
            throw new InvalidDataException();
        return buffer.ToArray();
    }
}
