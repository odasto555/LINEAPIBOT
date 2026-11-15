using LINEAPI;
using LineJobs.Configuration;
using LineJobs.Jobs;
using LineJobs.Storage;

namespace LineJobs.Endpoints;

internal static class AccountEndpoints
{
    internal static void MapAccounts(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/accounts");
        routes.MapGet("", ListAsync);
        routes.MapPost("", LinkAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, AccountStore accounts, CancellationToken ct) =>
        Results.Ok((await accounts.ListAsync(JobEndpoints.Owner(context), ct)).Select(id => new AccountResponse(id)));

    private static async Task<IResult> LinkAsync(
        LinkAccountRequest request, HttpContext context, AccountStore accounts, ApiSettings settings, LineApiClient line,
        CancellationToken ct)
    {
        if (request.AccessToken is not { Length: >= 1 and <= 8192 } token ||
            string.IsNullOrWhiteSpace(token) || token.Any(c => c <= ' ' || c > '~'))
            return JobEndpoints.Invalid("accessToken", "A valid LINE access token is required.");

        LineRequestContext lineContext = settings.ForAccount(token);
        try
        {
            var profile = await line.Profiles.GetAsync(lineContext, ct);
            if (!RequestValidation.TryUserId(profile.UserId, out var id))
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Invalid LINE account response.");

            var account = new AccountWrite(id, JobEndpoints.Owner(context), new AccountSecret(token));
            return await accounts.LinkAsync(account, ct) == StoreOutcome.Applied
                ? Results.Ok(new AccountResponse(id))
                : Results.Conflict(new ApiError("accountUnavailable"));
        }
        catch (LineApiException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Could not verify the LINE account.",
                extensions: new Dictionary<string, object?> { ["code"] = $"line{ex.Failure}" });
        }
    }
}
