using System.Security.Claims;
using LineJobs.Security;

namespace LineJobs.Endpoints;

internal static class AuthEndpoints
{
    internal static void MapLogin(this WebApplication app) =>
        app.MapPost("/v1/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting("login");

    internal static void MapAuth(this RouteGroupBuilder api)
    {
        api.MapGet("/auth/me", Me);
        api.MapPost("/auth/logout", LogoutAsync);
        api.MapPost("/admin/users", CreateUserAsync).RequireAuthorization(policy => policy.RequireRole(UserSessions.AdminRole));
    }

    private static async Task<IResult> LoginAsync(PasswordRequest request, UserSessions sessions, CancellationToken ct)
    {
        if (!UserSessions.ValidUsername(request.Username) || request.Password is not { Length: >= 1 and <= 128 })
            return InvalidLogin();

        var session = await sessions.LoginAsync(request.Username!, request.Password, ct);
        return session is null ? InvalidLogin() : Results.Ok(session);
    }

    private static IResult Me(HttpContext context) => Results.Ok(new UserResponse(
        JobEndpoints.Owner(context), context.User.IsInRole(UserSessions.AdminRole) ? UserSessions.AdminRole : UserSessions.MemberRole));

    private static async Task<IResult> LogoutAsync(HttpContext context, UserSessions sessions, CancellationToken ct)
    {
        var sessionHash = context.User.FindFirstValue(SessionAuthentication.SessionClaim)
            ?? throw new InvalidOperationException("Session authentication is required.");
        await sessions.LogoutAsync(JobEndpoints.Owner(context), sessionHash, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateUserAsync(PasswordRequest request, UserSessions sessions, CancellationToken ct)
    {
        if (!UserSessions.ValidUsername(request.Username))
            return JobEndpoints.Invalid("username", "Use 1 to 64 letters, digits, underscores, or hyphens.");
        if (!UserSessions.ValidNewPassword(request.Password))
            return JobEndpoints.Invalid("password", "Use a password with 12 to 128 characters.");

        var username = request.Username!;
        return await sessions.CreateMemberAsync(username, request.Password!, ct)
            ? Results.Json(new UserResponse(username, UserSessions.MemberRole), statusCode: StatusCodes.Status201Created)
            : Results.Conflict(new ApiError("usernameTaken"));
    }

    private static IResult InvalidLogin() =>
        Results.Json(new ApiError("invalidCredentials"), statusCode: StatusCodes.Status401Unauthorized);

    private sealed record PasswordRequest(string? Username, string? Password);
    private sealed record UserResponse(string Username, string Role);
}
