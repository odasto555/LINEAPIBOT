using LINEAPI;

namespace LineJobs.Configuration;

internal sealed class ApiSettings
{
    private ApiSettings() { }

    internal required string DatabasePath { get; init; }
    internal required byte[] StorageKey { get; init; }
    internal required IReadOnlyDictionary<string, byte[]> Clients { get; init; }
    internal required Uri LineOrigin { get; init; }
    internal required LineRequestContext AnonymousContext { get; init; }
    internal int Workers { get; init; }
    internal int QueueCapacity { get; init; }
    internal int MaxActivePerUser { get; init; }
    internal int OtpMinutes { get; init; }

    internal static ApiSettings Read(IConfiguration configuration, string root)
    {
        var clients = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var client in configuration.GetSection("Api:Clients").GetChildren())
        {
            var hash = client.Value;
            if (client.Key.Length is < 1 or > 64 || !client.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
                hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Api:Clients must map a client ID to a 64-character SHA-256 hex digest.");

            var bytes = Convert.FromHexString(hash);
            if (clients.Values.Any(value => value.AsSpan().SequenceEqual(bytes)))
                throw new InvalidOperationException("Each client must have a separate bearer key.");
            clients.Add(client.Key, bytes);
        }

        if (clients.Count == 0)
            throw new InvalidOperationException("Configure at least one Api:Clients entry.");

        var key = Convert.FromBase64String(Required(configuration, "Api:StorageKey"));
        if (key.Length != 32)
            throw new InvalidOperationException("Api:StorageKey must decode to 32 bytes.");

        var capacity = ReadInt(configuration, "Api:QueueCapacity", 500, 1, 100_000);
        return new ApiSettings
        {
            DatabasePath = Path.GetFullPath(configuration["Api:DatabasePath"] ?? "data/api.db", root),
            StorageKey = key,
            Clients = clients,
            LineOrigin = new Uri(Required(configuration, "Line:BaseUrl"), UriKind.Absolute),
            AnonymousContext = new LineRequestContext(
                Required(configuration, "Line:UserAgent"),
                Required(configuration, "Line:Application").Replace("\\t", "\t", StringComparison.Ordinal),
                Required(configuration, "Line:Locale")),
            Workers = ReadInt(configuration, "Api:Workers", 8, 1, 64),
            QueueCapacity = capacity,
            MaxActivePerUser = ReadInt(configuration, "Api:MaxActivePerUser", Math.Min(capacity, 25), 1, capacity),
            OtpMinutes = ReadInt(configuration, "Api:OtpMinutes", 5, 1, 15)
        };
    }

    internal LineRequestContext ForAccount(string token) => new(
        AnonymousContext.UserAgent, AnonymousContext.Application, AnonymousContext.Locale, token);

    private static string Required(IConfiguration configuration, string name) =>
        configuration[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configure {name}.");

    private static int ReadInt(IConfiguration configuration, string name, int fallback, int min, int max)
    {
        var value = configuration.GetValue(name, fallback);
        if (value < min || value > max)
            throw new InvalidOperationException($"{name} must be between {min} and {max}.");
        return value;
    }
}
