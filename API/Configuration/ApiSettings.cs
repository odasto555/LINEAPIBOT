using LINEAPI;

namespace LineJobs.Configuration;

internal sealed class ApiSettings
{
    private ApiSettings() { }

    internal required string MongoConnectionString { get; init; }
    internal required string MongoDatabaseName { get; init; }
    internal required byte[] StorageKey { get; init; }
    internal string? BootstrapUsername { get; init; }
    internal string? BootstrapPassword { get; init; }
    internal int TokenHours { get; init; }
    internal required Uri LineOrigin { get; init; }
    internal required string LineVersion { get; init; }
    internal required LineClientProfile FallbackProfile { get; init; }
    internal required string LineLocale { get; init; }
    internal int Workers { get; init; }
    internal int QueueCapacity { get; init; }
    internal int MaxActivePerUser { get; init; }
    internal int OtpMinutes { get; init; }

    internal static ApiSettings Read(IConfiguration configuration)
    {
        var key = Convert.FromBase64String(Required(configuration, "Api:StorageKey"));
        if (key.Length != 32)
            throw new InvalidOperationException("Api:StorageKey must decode to 32 bytes.");

        var capacity = ReadInt(configuration, "Api:QueueCapacity", 500, 1, 100_000);
        if (!Version.TryParse(Required(configuration, "Line:Version"), out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidOperationException("Line:Version must contain three numeric components, such as 26.7.1.");

        var lineVersion = version.ToString(3);
        var fallback = AndroidRandomizer.Default(lineVersion);
        var userAgent = configuration["Line:UserAgent"];
        var application = configuration["Line:Application"];
        return new ApiSettings
        {
            MongoConnectionString = Required(configuration, "MongoDb:ConnectionString"),
            MongoDatabaseName = Required(configuration, "MongoDb:Database"),
            StorageKey = key,
            BootstrapUsername = configuration["Auth:BootstrapUsername"],
            BootstrapPassword = configuration["Auth:BootstrapPassword"],
            TokenHours = ReadInt(configuration, "Auth:TokenHours", 12, 1, 168),
            LineOrigin = new Uri(Required(configuration, "Line:BaseUrl"), UriKind.Absolute),
            LineVersion = lineVersion,
            FallbackProfile = new LineClientProfile(
                string.IsNullOrWhiteSpace(userAgent) ? fallback.UserAgent : userAgent,
                string.IsNullOrWhiteSpace(application) ? fallback.Application : application.Replace("\\t", "\t", StringComparison.Ordinal)),
            LineLocale = Required(configuration, "Line:Locale"),
            Workers = ReadInt(configuration, "Api:Workers", 8, 1, 64),
            QueueCapacity = capacity,
            MaxActivePerUser = ReadInt(configuration, "Api:MaxActivePerUser", Math.Min(capacity, 25), 1, capacity),
            OtpMinutes = ReadInt(configuration, "Api:OtpMinutes", 5, 1, 15)
        };
    }

    internal LineRequestContext LineContext(LineClientProfile profile, string? accessToken = null,
        Func<LineRpcTrace, CancellationToken, Task>? trace = null) =>
        new(profile.UserAgent, profile.Application, LineLocale, accessToken, trace);

    internal LineRequestContext ForAccount(string accessToken, LineClientProfile? profile = null) =>
        LineContext(AndroidRandomizer.WithLineVersion(profile ?? FallbackProfile, LineVersion), accessToken);

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
