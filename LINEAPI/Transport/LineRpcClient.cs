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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(httpClient.Timeout);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new LineApiException(operation, LineFailure.Http, response.StatusCode);

            var bytes = await ReadResponseAsync(response.Content, deadline.Token);
            return await CompactRpc.DecodeAsync(operation, bytes, sequenceId, voidResult, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LineApiException(operation, LineFailure.Network);
        }
        catch (HttpRequestException)
        {
            throw new LineApiException(operation, LineFailure.Network);
        }
        catch (Exception ex) when (ex is TException or InvalidDataException or OverflowException)
        {
            throw new LineApiException(operation, LineFailure.Protocol);
        }
        catch (IOException)
        {
            throw new LineApiException(operation, LineFailure.Network);
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
