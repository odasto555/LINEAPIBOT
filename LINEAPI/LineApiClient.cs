using System.Net;
using LINEAPI.Friends;
using LINEAPI.Groups;
using LINEAPI.Profiles;
using LINEAPI.Registration;
using LINEAPI.Transport;

namespace LINEAPI;

public sealed class LineApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public LineApiClient(Uri baseUri, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps ||
            baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 ||
            baseUri.Fragment.Length != 0 || baseUri.AbsolutePath != "/")
        {
            throw new ArgumentException("An HTTPS origin is required.", nameof(baseUri));
        }

        var requestTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (requestTimeout != Timeout.InfiniteTimeSpan &&
            (requestTimeout <= TimeSpan.Zero || requestTimeout.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(handler) { Timeout = requestTimeout };

        var rpc = new LineRpcClient(_httpClient, baseUri);
        Registration = new RegistrationClient(rpc);
        Profiles = new ProfileClient(rpc);
        Friends = new FriendClient(rpc);
        Groups = new GroupClient(rpc);
    }

    public RegistrationClient Registration { get; }
    public ProfileClient Profiles { get; }
    public FriendClient Friends { get; }
    public GroupClient Groups { get; }

    public void Dispose() => _httpClient.Dispose();
}
