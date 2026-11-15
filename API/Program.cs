using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LINEAPI;
using LineJobs.Configuration;
using LineJobs.Endpoints;
using LineJobs.Jobs;
using LineJobs.Security;
using LineJobs.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;

namespace LineJobs;

public static class Program
{
    public static Task Main(string[] args) => Build(args).RunAsync();

    internal static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Configuration["PORT"] is { Length: > 0 } portValue)
        {
            if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("PORT must contain a valid TCP port.");
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        }

        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(SessionAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthentication>(SessionAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("client", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        builder.Services.AddSingleton(sp => ApiSettings.Read(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<SecretStore>();
        builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
        builder.Services.AddSingleton<IPasswordHasher<UserDocument>, PasswordHasher<UserDocument>>();
        builder.Services.AddSingleton<UserSessions>();
        builder.Services.AddSingleton<Database>();
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<JobStore>();
        builder.Services.AddSingleton(sp => new LineApiClient(sp.GetRequiredService<ApiSettings>().LineOrigin));
        builder.Services.AddHostedService<DatabaseStartup>();
        builder.Services.AddHostedService<JobWorker>();
        configure?.Invoke(builder);

        var app = builder.Build();
        _ = app.Services.GetRequiredService<Database>();
        _ = app.Services.GetRequiredService<LineApiClient>();

        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/healthz", HealthAsync);
        app.MapLogin();
        var api = app.MapGroup("/v1").RequireAuthorization().RequireRateLimiting("client");
        api.MapAuth();
        api.MapAccounts();
        api.MapJobs();
        api.MapMonitor();
        app.MapFallbackToFile("/monitor", "index.html");
        return app;
    }

    private static async Task<IResult> HealthAsync(Database database, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await database.PingAsync(deadline.Token);
            return Results.Ok(new { status = "ok" });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is MongoException or TimeoutException or OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
