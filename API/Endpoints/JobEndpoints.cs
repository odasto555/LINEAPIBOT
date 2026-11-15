using System.Security.Claims;
using LineJobs.Jobs;
using LineJobs.Security;
using LineJobs.Storage;

namespace LineJobs.Endpoints;

internal static class JobEndpoints
{
    internal static void MapJobs(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/jobs");
        routes.MapPost("/registration", Register);
        routes.MapPost("/add-friends", AddFriends);
        routes.MapPost("/create-group", CreateGroup);
        routes.MapPost("/{id:guid}/otp", SubmitOtp);
        routes.MapGet("/{id:guid}", Get);
        routes.MapGet("", List);
    }

    private static Task<IResult> Register(RegisterRequest request, HttpContext context, JobStore jobs, SecretStore secrets, CancellationToken ct)
    {
        if (!RequestValidation.IsE164(request.PhoneNumber))
            return Task.FromResult(Invalid("phoneNumber", "Use an international phone number in E.164 format."));
        if (!RequestValidation.IsCountry(request.CountryCode))
            return Task.FromResult(Invalid("countryCode", "Use a two-letter country code."));
        if (!RequestValidation.HasText(request.DisplayName, 128) || !RequestValidation.HasText(request.Password, 128) ||
            !RequestValidation.HasText(request.DeviceId, 128) || !RequestValidation.HasText(request.DeviceModel, 128))
            return Task.FromResult(Invalid("registration", "Display name, password, device ID, and device model are required."));

        var work = new RegistrationWork(request.PhoneNumber!, request.CountryCode!.ToUpperInvariant(),
            request.DisplayName!.Trim(), request.Password!, request.DeviceId!, request.DeviceModel!);
        return Enqueue(context, jobs, secrets, JobKind.Registration,
            $"phone:{secrets.Fingerprint("registration-phone", work.PhoneNumber)}", work, "openSession", ct);
    }

    private static async Task<IResult> AddFriends(AddFriendsRequest request, HttpContext context, JobStore jobs, AccountStore accounts, SecretStore secrets, CancellationToken ct)
    {
        if (!RequestValidation.TryUserId(request.AccountId, out var id))
            return Invalid("accountId", "A LINE account ID is required.");
        if (request.PhoneNumbers is not { Length: >= 1 and <= 100 } phones ||
            !phones.All(RequestValidation.IsContact))
            return Invalid("phoneNumbers", "Provide 1 to 100 phone numbers.");
        if (request.Revision is not >= 0 || request.SequenceId is < 0)
            return Invalid("revision", "Revision is required and both revision and sequence ID must be nonnegative.");
        if (!await accounts.ExistsAsync(Owner(context), id, ct))
            return Results.NotFound(new ApiError("accountNotFound"));

        var work = new AddFriendsWork(id, phones.Select(phone => phone.TrimStart('+')).ToArray(),
            request.Revision.Value, request.SequenceId ?? 1);
        return await Enqueue(context, jobs, secrets, JobKind.AddFriends, AccountStore.Resource(id), work, "syncContacts", ct);
    }

    private static async Task<IResult> CreateGroup(CreateGroupRequest request, HttpContext context, JobStore jobs, AccountStore accounts, SecretStore secrets, CancellationToken ct)
    {
        if (!RequestValidation.TryUserId(request.AccountId, out var id))
            return Invalid("accountId", "A LINE account ID is required.");
        if (!RequestValidation.HasText(request.Name, 128))
            return Invalid("name", "A group name is required.");
        if (request.UserIds is not { Length: >= 1 and <= 500 } users)
            return Invalid("userIds", "Provide 1 to 500 LINE user IDs.");
        if (request.Revision is not >= 0)
            return Invalid("revision", "A nonnegative revision is required.");

        var members = new List<string>(users.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in users)
        {
            if (!RequestValidation.TryUserId(raw, out var member))
                return Invalid("userIds", "All members must have a valid LINE user ID.");
            if (seen.Add(member))
                members.Add(member);
        }

        if (!await accounts.ExistsAsync(Owner(context), id, ct))
            return Results.NotFound(new ApiError("accountNotFound"));
        var work = new CreateGroupWork(id, request.Name!.Trim(), members.ToArray(), request.Revision.Value);
        return await Enqueue(context, jobs, secrets, JobKind.CreateGroup, AccountStore.Resource(id), work, "createGroup", ct);
    }

    private static async Task<IResult> SubmitOtp(Guid id, OtpRequest request, JobStore jobs, HttpContext context, CancellationToken ct)
    {
        if (request.Code is not { Length: >= 4 and <= 12 } code || !code.All(char.IsAsciiDigit))
            return Invalid("code", "Use the numeric OTP sent by LINE.");

        return (await jobs.SubmitOtpAsync(Owner(context), id, code, ct)) switch
        {
            StoreOutcome.Applied => Accepted(id),
            StoreOutcome.NotFound => Results.NotFound(new ApiError("jobNotFound")),
            StoreOutcome.Expired => Results.Json(new ApiError("otpExpired"), statusCode: StatusCodes.Status410Gone),
            StoreOutcome.WrongState => Results.Conflict(new ApiError("jobNotAwaitingOtp")),
            _ => throw new InvalidOperationException()
        };
    }

    private static async Task<IResult> Get(Guid id, JobStore jobs, HttpContext context, CancellationToken ct) =>
        await jobs.GetAsync(Owner(context), id, ct) is { } job ? Results.Ok(job) : Results.NotFound(new ApiError("jobNotFound"));

    private static async Task<IResult> List(int? limit, JobStore jobs, HttpContext context, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return Invalid("limit", "Use a limit from 1 to 100.");
        return Results.Ok(await jobs.ListAsync(Owner(context), limit ?? 25, ct));
    }

    private static async Task<IResult> Enqueue<T>(HttpContext context, JobStore jobs, SecretStore secrets,
        JobKind kind, string resource, T work, string stage, CancellationToken ct)
    {
        string? key = null;
        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            key = values.Count == 1 ? values[0] : null;
            if (key is not { Length: >= 1 and <= 128 } ||
                !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return Invalid("Idempotency-Key", "Use 1 to 128 letters, digits, hyphens, underscores, or periods.");
        }

        var id = Guid.NewGuid();
        var result = await jobs.EnqueueAsync(new EnqueueRequest(id, Owner(context), kind, resource, jobs.Seal(id, work),
            stage, key, secrets.Fingerprint($"request:{kind}", work)), ct);
        return result.Outcome switch
        {
            StoreOutcome.Applied => Accepted(result.Id),
            StoreOutcome.DuplicateAccount => Results.Conflict(new ApiError("accountBusy")),
            StoreOutcome.DuplicateRequest => Results.Conflict(new ApiError("idempotencyKeyReused")),
            StoreOutcome.Full => Results.Json(new ApiError("queueFull"), statusCode: StatusCodes.Status429TooManyRequests),
            _ => throw new InvalidOperationException()
        };
    }

    internal static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Client authentication is required.");

    internal static IResult Invalid(string property, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [property] = [message] });

    private static IResult Accepted(Guid id)
    {
        var location = $"/v1/jobs/{id}";
        return Results.Accepted(location, new AcceptedJob(id, location));
    }
}
