namespace LineJobs.Jobs;

internal enum JobKind
{
    Registration,
    AddFriends,
    CreateGroup
}

internal enum JobState
{
    Queued,
    Running,
    AwaitingOtp,
    Succeeded,
    Failed,
    Expired,
    Interrupted
}

internal sealed record JobError(string Code, string? Operation = null, int? UpstreamStatus = null, int? RpcCode = null);
internal sealed record JobResult(string? AccountId = null, string? GroupId = null, IReadOnlyList<string>? FriendIds = null);

internal sealed record JobStatus(
    Guid Id, JobKind Kind, JobState State, string Stage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? OtpExpiresAt, JobResult? Result, JobError? Error);

internal sealed record ClaimedJob(Guid Id, string Owner, JobKind Kind, string Payload);
internal sealed record AccountSecret(string AccessToken, string? RefreshToken = null);
internal sealed record AccountWrite(string Id, string Owner, string EncryptedSecret);
internal sealed record RegistrationWork(
    string PhoneNumber, string CountryCode, string DisplayName, string Password,
    string DeviceId, string DeviceModel, string? SessionId = null, string? Otp = null);

internal sealed record AddFriendsWork(string AccountId, string[] PhoneNumbers, int Revision, int SequenceId);
internal sealed record CreateGroupWork(string AccountId, string Name, string[] UserIds, int Revision);

internal sealed class JobRejectedException(string code) : Exception(code)
{
    internal string Code => Message;
}

internal enum StoreOutcome { Applied, DuplicateAccount, DuplicateRequest, Full, NotFound, WrongState, Expired }
internal sealed record EnqueueResult(StoreOutcome Outcome, Guid Id);
internal sealed record EnqueueRequest(
    Guid Id, string Owner, JobKind Kind, string ResourceKey, string Payload, string Stage, string? RequestId, string Fingerprint);
