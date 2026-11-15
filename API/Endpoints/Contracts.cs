namespace LineJobs.Endpoints;

internal sealed record RegisterRequest(
    string? PhoneNumber, string? CountryCode, string? DisplayName, string? Password, string? DeviceId, string? DeviceModel);

internal sealed record AddFriendsRequest(string? AccountId, string[]? PhoneNumbers, int? Revision, int? SequenceId);
internal sealed record CreateGroupRequest(string? AccountId, string? Name, string[]? UserIds, int? Revision);
internal sealed record OtpRequest(string? Code);
internal sealed record LinkAccountRequest(string? AccessToken);
internal sealed record AccountResponse(string AccountId);
internal sealed record AcceptedJob(Guid JobId, string StatusUrl);
internal sealed record ApiError(string Code);
