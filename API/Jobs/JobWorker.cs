using LINEAPI;
using LINEAPI.Registration;
using LineJobs.Configuration;
using LineJobs.Storage;
using MongoDB.Driver;

namespace LineJobs.Jobs;

internal sealed class JobWorker(
    JobStore jobs, AccountStore accounts, ApiSettings settings, LineApiClient line, ILogger<JobWorker> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, settings.Workers).Select(_ => RunWorkerAsync(stoppingToken))
            .Append(ExpireJobsAsync(stoppingToken)));

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ClaimedJob? job;
            try
            {
                job = await jobs.ClaimAsync(ct);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                logger.LogWarning("Queue read failed ({ExceptionType}).", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                continue;
            }

            await RunClaimedJobAsync(job, ct);
        }
    }

    private async Task RunClaimedJobAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(job, execution);
        try
        {
            await ExecuteJobAsync(job, execution.Token);
        }
        catch (JobLeaseLostException)
        {
            logger.LogWarning("Worker lease was lost for job {JobId}.", job.Id);
        }
        catch (JobRejectedException ex)
        {
            await FailAsync(job, new JobError(ex.Code));
        }
        catch (LineApiException ex)
        {
            await FailAsync(job, new JobError($"line{ex.Failure}", ex.Operation, (int?)ex.HttpStatus, ex.RpcCode));
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            await FailAsync(job, new JobError("serverInterrupted"), JobState.Interrupted);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            logger.LogError("Could not persist job {JobId} ({ExceptionType}).", job.Id, ex.GetType().Name);
            await FailAsync(job, new JobError("serverInterrupted"), JobState.Interrupted);
        }
        catch (Exception ex)
        {
            logger.LogError("Job {JobId} failed ({ExceptionType}).", job.Id, ex.GetType().Name);
            await FailAsync(job, new JobError("jobFailed"));
        }
        finally
        {
            await execution.CancelAsync();
            await heartbeat;
        }
    }

    private async Task MaintainLeaseAsync(ClaimedJob job, CancellationTokenSource execution)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(execution.Token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                if (await jobs.RenewAsync(job, deadline.Token))
                    continue;

                await execution.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Could not renew the worker lease for {JobId} ({ExceptionType}).", job.Id, ex.GetType().Name);
            await execution.CancelAsync();
        }
    }

    private async Task ExecuteJobAsync(ClaimedJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case JobKind.Registration:
                await RegisterAsync(job, ct);
                break;
            case JobKind.AddFriends:
                var contacts = jobs.Read<AddFriendsWork>(job);
                var friendContext = settings.ForAccount((await accounts.GetAsync(job.Owner, contacts.AccountId, ct)).AccessToken);
                var ids = await line.Friends.SyncContactsAsync(
                    friendContext, contacts.PhoneNumbers, contacts.Revision, contacts.SequenceId, ct);
                await jobs.CompleteAsync(job, new JobResult(AccountId: contacts.AccountId, FriendIds: ids), null, ct);
                break;
            case JobKind.CreateGroup:
                var group = jobs.Read<CreateGroupWork>(job);
                var groupContext = settings.ForAccount((await accounts.GetAsync(job.Owner, group.AccountId, ct)).AccessToken);
                var created = await line.Groups.CreateAsync(groupContext, group.Name, group.UserIds, group.Revision, ct);
                await jobs.CompleteAsync(job, new JobResult(AccountId: group.AccountId, GroupId: created.GroupId), null, ct);
                break;
            default:
                throw new InvalidOperationException("Unknown job kind.");
        }
    }

    private async Task RegisterAsync(ClaimedJob job, CancellationToken ct)
    {
        var work = jobs.Read<RegistrationWork>(job);
        var context = settings.AnonymousContext;
        if (work.SessionId is null)
        {
            await jobs.SetStageAsync(job, "openSession", ct);
            var session = await line.Registration.OpenSessionAsync(context, ct);
            await jobs.SetStageAsync(job, "checkRegistrationMethod", ct);
            await line.Registration.GetCountryInfoAsync(context, session, cancellationToken: ct);
            var allowed = await line.Registration.GetAllowedMethodAsync(context, session, work.CountryCode, ct);
            if (allowed != RegistrationMethod.Phone)
                throw new JobRejectedException("phoneRegistrationUnavailable");

            var verification = await line.Registration.GetPhoneVerificationAsync(context, session,
                new RegistrationDevice(work.DeviceId, work.DeviceModel), work.PhoneNumber, work.CountryCode, ct);
            if (!verification.AvailableMethods.Contains(PhoneVerificationMethod.Sms))
                throw new JobRejectedException("smsUnavailable");

            await jobs.SetStageAsync(job, "requestOtp", ct);
            await line.Registration.RequestCodeAsync(
                context, session, verification.FormattedNumber, work.CountryCode, PhoneVerificationMethod.Sms, ct);
            await jobs.AwaitOtpAsync(job, work with { SessionId = session, PhoneNumber = verification.FormattedNumber }, ct);
            return;
        }

        if (work.Otp is null)
            throw new JobRejectedException("otpMissing");

        var verified = await line.Registration.VerifyCodeAsync(
            context, work.SessionId, work.PhoneNumber, work.CountryCode, work.Otp, ct);
        if (verified.AccountExists is true)
            throw new JobRejectedException("accountAlreadyExists");
        if (verified.AccountExists is not false || verified.AllowedToRegister is false)
            throw new JobRejectedException("registrationNotAllowed");

        await jobs.SetStageAsync(job, "validateProfile", ct);
        await line.Registration.ValidateProfileAsync(context, work.SessionId, work.DisplayName, ct);
        await jobs.SetStageAsync(job, "setPassword", ct);
        await line.Registration.SetPasswordAsync(context, work.SessionId, work.Password, ct);
        await jobs.SetStageAsync(job, "registerAccount", ct);
        var registered = await line.Registration.RegisterAsync(context, work.SessionId, ct);
        if (!RequestValidation.TryUserId(registered.UserId, out var accountId))
            throw new JobRejectedException("invalidUpstreamAccount");

        var account = accounts.Protect(accountId, job.Owner, new AccountSecret(registered.AccessToken, registered.RefreshToken));
        await jobs.CompleteAsync(job, new JobResult(AccountId: accountId), account, ct);
    }

    private async Task ExpireJobsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await jobs.ExpireAsync(ct);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                logger.LogWarning("Queue expiration failed ({ExceptionType}).", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task FailAsync(ClaimedJob job, JobError error, JobState state = JobState.Failed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await jobs.FailAsync(job, error, state, deadline.Token);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            logger.LogError("Could not record {ErrorCode} for {JobId} ({ExceptionType}).", error.Code, job.Id, ex.GetType().Name);
        }
    }
}
