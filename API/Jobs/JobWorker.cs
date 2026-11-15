using LINEAPI;
using LINEAPI.Registration;
using LineJobs.Configuration;
using LineJobs.Storage;
using Microsoft.Data.Sqlite;

namespace LineJobs.Jobs;

internal sealed class JobWorker(
    JobStore jobs, AccountStore accounts, ApiSettings settings, LineApiClient line, IHostApplicationLifetime lifetime, ILogger<JobWorker> logger)
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
                job = jobs.Claim();
            }
            catch (SqliteException ex)
            {
                logger.LogWarning("Queue read failed with SQLite code {Code}.", ex.SqliteErrorCode);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                continue;
            }

            try
            {
                await ExecuteJobAsync(job, ct);
            }
            catch (JobRejectedException ex)
            {
                Fail(job.Id, new JobError(ex.Code));
            }
            catch (LineApiException ex)
            {
                Fail(job.Id, new JobError($"line{ex.Failure}", ex.Operation, (int?)ex.HttpStatus, ex.RpcCode));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Fail(job.Id, new JobError("serverInterrupted"), JobState.Interrupted);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Job {JobId} failed with {ExceptionType}.", job.Id, ex.GetType().Name);
                Fail(job.Id, new JobError("jobFailed"));
            }
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
                var friendContext = settings.ForAccount(accounts.Get(job.Owner, contacts.AccountId).AccessToken);
                var ids = await line.Friends.SyncContactsAsync(
                    friendContext, contacts.PhoneNumbers, contacts.Revision, contacts.SequenceId, ct);
                jobs.Complete(job.Id, new JobResult(AccountId: contacts.AccountId, FriendIds: ids));
                break;
            case JobKind.CreateGroup:
                var group = jobs.Read<CreateGroupWork>(job);
                var groupContext = settings.ForAccount(accounts.Get(job.Owner, group.AccountId).AccessToken);
                var created = await line.Groups.CreateAsync(groupContext, group.Name, group.UserIds, group.Revision, ct);
                jobs.Complete(job.Id, new JobResult(AccountId: group.AccountId, GroupId: created.GroupId));
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
            jobs.SetStage(job.Id, "openSession");
            var session = await line.Registration.OpenSessionAsync(context, ct);
            jobs.SetStage(job.Id, "checkRegistrationMethod");
            await line.Registration.GetCountryInfoAsync(context, session, cancellationToken: ct);
            var allowed = await line.Registration.GetAllowedMethodAsync(context, session, work.CountryCode, ct);
            if (allowed != RegistrationMethod.Phone)
                throw new JobRejectedException("phoneRegistrationUnavailable");

            var verification = await line.Registration.GetPhoneVerificationAsync(context, session,
                new RegistrationDevice(work.DeviceId, work.DeviceModel), work.PhoneNumber, work.CountryCode, ct);
            if (!verification.AvailableMethods.Contains(PhoneVerificationMethod.Sms))
                throw new JobRejectedException("smsUnavailable");

            jobs.SetStage(job.Id, "requestOtp");
            await line.Registration.RequestCodeAsync(
                context, session, verification.FormattedNumber, work.CountryCode, PhoneVerificationMethod.Sms, ct);
            jobs.AwaitOtp(job.Id, work with { SessionId = session, PhoneNumber = verification.FormattedNumber });
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

        jobs.SetStage(job.Id, "validateProfile");
        await line.Registration.ValidateProfileAsync(context, work.SessionId, work.DisplayName, ct);
        jobs.SetStage(job.Id, "setPassword");
        await line.Registration.SetPasswordAsync(context, work.SessionId, work.Password, ct);
        jobs.SetStage(job.Id, "registerAccount");
        var registered = await line.Registration.RegisterAsync(context, work.SessionId, ct);
        if (!RequestValidation.TryUserId(registered.UserId, out var accountId))
            throw new JobRejectedException("invalidUpstreamAccount");

        var account = accounts.Protect(accountId, job.Owner, new AccountSecret(registered.AccessToken, registered.RefreshToken));
        jobs.Complete(job.Id, new JobResult(AccountId: accountId), account);
    }

    private async Task ExpireJobsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                jobs.Expire();
            }
            catch (SqliteException ex)
            {
                logger.LogWarning("Queue expiration failed with SQLite code {Code}.", ex.SqliteErrorCode);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private void Fail(Guid id, JobError error, JobState state = JobState.Failed)
    {
        try
        {
            jobs.Fail(id, error, state);
        }
        catch (Exception ex)
        {
            logger.LogCritical("Could not update job {JobId} after {ErrorCode} ({ExceptionType}).", id, error.Code, ex.GetType().Name);
            lifetime.StopApplication();
        }
    }
}
