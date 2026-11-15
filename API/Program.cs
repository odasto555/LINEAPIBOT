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

namespace LineJobs;

public static class Program
{
    public static Task Main(string[] args) => Build(args).RunAsync();

    internal static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(ClientAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ClientAuthentication>(ClientAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("client", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        builder.Services.AddSingleton(sp => ApiSettings.Read(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
        builder.Services.AddSingleton<SecretStore>();
        builder.Services.AddSingleton<Database>();
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<JobStore>();
        builder.Services.AddSingleton(sp => new LineApiClient(sp.GetRequiredService<ApiSettings>().LineOrigin));
        builder.Services.AddHostedService<JobWorker>();
        configure?.Invoke(builder);

        var app = builder.Build();
        _ = app.Services.GetRequiredService<Database>();
        _ = app.Services.GetRequiredService<LineApiClient>();

        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        var api = app.MapGroup("/v1").RequireAuthorization().RequireRateLimiting("client");
        api.MapAccounts();
        api.MapJobs();
        return app;
    }
}
