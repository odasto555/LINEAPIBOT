using LINEAPI;
using LineJobs.Configuration;
using LineJobs.Jobs;
using LineJobs.Security;
using MongoDB.Driver;

namespace LineJobs.Storage;

internal sealed class JobStore(Database database, ApiSettings settings, SecretStore secrets, AccountStore accounts)
{
    private static readonly FilterDefinitionBuilder<JobDocument> Filter = Builders<JobDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<JobDocument> Update = Builders<JobDocument>.Update;
    private static readonly FilterDefinition<JobDocument> Active = Filter.Eq(x => x.Active, true);

    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    internal async Task<EnqueueResult> EnqueueAsync(EnqueueRequest job, CancellationToken ct)
    {
        var resource = Filter.Eq(x => x.ResourceKey, job.ResourceKey);
        var cutoff = DateTime.UtcNow;
        await database.Jobs.UpdateManyAsync(ExpiredOtp(cutoff) & resource,
            Terminal(JobState.Expired, cutoff).Set(x => x.Error, JobDocument.Encode(new JobError("otpExpired"))),
            cancellationToken: ct);

        return await database.WithAdmissionLockAsync(async (session, token) =>
        {
            if (job.RequestId is not null)
            {
                var existing = await database.Jobs.Find(session, item => item.Owner == job.Owner && item.RequestId == job.RequestId)
                    .FirstOrDefaultAsync(token);
                if (existing is not null)
                    return existing.Fingerprint == job.Fingerprint
                        ? new EnqueueResult(StoreOutcome.Applied, Guid.Parse(existing.Id))
                        : new EnqueueResult(StoreOutcome.DuplicateRequest, Guid.Empty);
            }

            var now = DateTime.UtcNow;
            if (await database.Jobs.Find(session, Active & resource).AnyAsync(token))
                return new EnqueueResult(StoreOutcome.DuplicateAccount, Guid.Empty);

            var total = await database.Jobs.CountDocumentsAsync(session, Active, cancellationToken: token);
            var count = await database.Jobs.CountDocumentsAsync(session, Active & Filter.Eq(x => x.Owner, job.Owner),
                cancellationToken: token);
            if (total >= settings.QueueCapacity || count >= settings.MaxActivePerUser)
                return new EnqueueResult(StoreOutcome.Full, Guid.Empty);

            await database.Jobs.InsertOneAsync(session, new JobDocument
            {
                Id = job.Id.ToString(),
                Owner = job.Owner,
                Kind = job.Kind,
                ResourceKey = job.ResourceKey,
                State = JobState.Queued,
                Active = true,
                Stage = job.Stage,
                Payload = job.Payload,
                CreatedAt = now,
                UpdatedAt = now,
                RequestId = job.RequestId,
                Fingerprint = job.Fingerprint
            }, cancellationToken: token);
            return new EnqueueResult(StoreOutcome.Applied, job.Id);
        }, ct);
    }

    internal async Task<JobStatus?> GetAsync(string owner, Guid id, CancellationToken ct) =>
        (await database.Jobs.Find(Owned(owner, id)).FirstOrDefaultAsync(ct))?.Status();

    internal async Task<LineRpcTrace[]?> GetLineLogAsync(string owner, Guid id, CancellationToken ct)
    {
        var events = await database.LineLogs.Find(x => x.Id == id.ToString() && x.Owner == owner)
            .Project(x => x.Events).FirstOrDefaultAsync(ct);
        if (events is not null)
            return events;

        return await database.Jobs.Find(Owned(owner, id)).Project(x => x.Id).AnyAsync(ct) ? [] : null;
    }

    internal Task AppendLineLogAsync(ClaimedJob job, LineRpcTrace entry, CancellationToken ct) =>
        database.LineLogs.UpdateOneAsync(x => x.Id == job.Id.ToString() && x.Owner == job.Owner,
            Builders<LineLogDocument>.Update.SetOnInsert(x => x.Owner, job.Owner)
                .PushEach(x => x.Events, [entry], slice: -48),
            new UpdateOptions { IsUpsert = true }, ct);

    internal async Task<IReadOnlyList<JobStatus>> ListAsync(string owner, int limit, CancellationToken ct)
    {
        var items = await database.Jobs.Find(x => x.Owner == owner).SortByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id).Limit(limit).ToListAsync(ct);
        return items.Select(x => x.Status()).ToArray();
    }

    internal async Task<IReadOnlyList<JobActivity>> MonitorAsync(bool? active, int limit, CancellationToken ct)
    {
        var filter = active is { } value ? Filter.Eq(x => x.Active, value) : Filter.Empty;
        var jobs = await database.Jobs.Find(filter).SortByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id).Limit(limit)
            .Project(x => new { x.Id, x.Owner, x.Kind, x.State, x.Stage, x.CreatedAt, x.UpdatedAt, x.Error })
            .ToListAsync(ct);
        return jobs.Select(x => new JobActivity(x.Id, x.Owner, x.Kind, x.State, x.Stage,
            x.CreatedAt, x.UpdatedAt, JobDocument.DecodeError(x.Error))).ToArray();
    }

    internal async Task<ClaimedJob?> ClaimAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseId = Guid.NewGuid().ToString();
        var filter = Filter.Eq(x => x.State, JobState.Queued) &
                     (Filter.Eq(x => x.OtpExpiresAt, null) | Filter.Gt(x => x.OtpExpiresAt, now));
        var update = Update.Set(x => x.State, JobState.Running).Set(x => x.UpdatedAt, now)
            .Set(x => x.LeaseId, leaseId).Set(x => x.LeaseUntil, now + LeaseDuration);
        var job = await database.Jobs.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<JobDocument>
        {
            Sort = Builders<JobDocument>.Sort.Ascending(x => x.UpdatedAt).Ascending(x => x.Id),
            ReturnDocument = ReturnDocument.After
        }, ct);

        return job is null ? null : new ClaimedJob(Guid.Parse(job.Id), job.Owner, job.Kind, job.Payload!, leaseId);
    }

    internal async Task<bool> RenewAsync(ClaimedJob job, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var result = await database.Jobs.UpdateOneAsync(Running(job, now),
            Update.Set(x => x.LeaseUntil, now + LeaseDuration), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    internal Task SetStageAsync(ClaimedJob job, string stage, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return UpdateOwnedAsync(job, Update.Set(x => x.Stage, stage).Set(x => x.UpdatedAt, now), now, ct);
    }

    internal Task StartRegistrationAsync(ClaimedJob job, RegistrationWork work, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return UpdateOwnedAsync(job, Update.Set(x => x.Stage, "openSession").Set(x => x.Payload, Seal(job.Id, work))
            .Set(x => x.UpdatedAt, now), now, ct);
    }

    internal Task AwaitOtpAsync(ClaimedJob job, RegistrationWork work, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return UpdateOwnedAsync(job, Update.Set(x => x.State, JobState.AwaitingOtp).Set(x => x.Stage, "awaitingOtp")
            .Set(x => x.Payload, Seal(job.Id, work)).Set(x => x.UpdatedAt, now)
            .Set(x => x.OtpExpiresAt, now.AddMinutes(settings.OtpMinutes))
            .Unset(x => x.LeaseUntil).Unset(x => x.LeaseId), now, ct);
    }

    internal async Task<StoreOutcome> SubmitOtpAsync(string owner, Guid id, string code, CancellationToken ct)
    {
        var owned = Owned(owner, id);
        var existing = await database.Jobs.Find(owned).FirstOrDefaultAsync(ct);
        if (existing is null)
            return StoreOutcome.NotFound;
        if (existing.State == JobState.Expired)
            return StoreOutcome.Expired;
        if (existing.State != JobState.AwaitingOtp || existing.Kind != JobKind.Registration)
            return StoreOutcome.WrongState;

        var now = DateTime.UtcNow;
        if (existing.OtpExpiresAt > now)
        {
            var work = secrets.Open<RegistrationWork>(Scope(id), existing.Payload!);
            now = DateTime.UtcNow;
            var ready = owned & Filter.Eq(x => x.State, JobState.AwaitingOtp) & Filter.Gt(x => x.OtpExpiresAt, now);
            var updated = await database.Jobs.UpdateOneAsync(ready,
                Update.Set(x => x.State, JobState.Queued).Set(x => x.Stage, "verifyOtp")
                    .Set(x => x.Payload, Seal(id, work with { Otp = code })).Set(x => x.UpdatedAt, now),
                cancellationToken: ct);
            if (updated.MatchedCount == 1)
                return StoreOutcome.Applied;
        }

        var expired = await database.Jobs.UpdateOneAsync(owned & Filter.Eq(x => x.State, JobState.AwaitingOtp)
            & Filter.Lte(x => x.OtpExpiresAt, now), Terminal(JobState.Expired, now)
            .Set(x => x.Error, JobDocument.Encode(new JobError("otpExpired"))), cancellationToken: ct);

        if (expired.MatchedCount == 1)
            return StoreOutcome.Expired;
        var current = await database.Jobs.Find(owned).FirstOrDefaultAsync(ct);
        return current?.State == JobState.Expired ? StoreOutcome.Expired : StoreOutcome.WrongState;
    }

    internal async Task CompleteAsync(ClaimedJob job, JobResult result, AccountWrite? account, CancellationToken ct)
    {
        var serialized = JobDocument.Encode(result);
        if (account is null)
        {
            var now = DateTime.UtcNow;
            await UpdateOwnedAsync(job, Terminal(JobState.Succeeded, now).Set(x => x.Stage, "done")
                .Set(x => x.Result, serialized), now, ct);
            return;
        }

        await database.WithAdmissionLockAsync(async (session, token) =>
        {
            var now = DateTime.UtcNow;
            var update = Terminal(JobState.Succeeded, now).Set(x => x.Stage, "done").Set(x => x.Result, serialized);
            var updated = await database.Jobs.UpdateOneAsync(session, Running(job, now), update, cancellationToken: token);
            if (updated.MatchedCount != 1)
                throw new JobLeaseLostException();

            if (!await accounts.SaveAsync(session, account, token))
                throw new JobRejectedException("accountAlreadyLinked");
            return true;
        }, ct);
    }

    internal Task FailAsync(ClaimedJob job, JobError error, JobState state, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return database.Jobs.UpdateOneAsync(Running(job, now),
            Terminal(state, now).Set(x => x.Error, JobDocument.Encode(error)), cancellationToken: ct);
    }

    internal async Task ExpireAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await database.Jobs.UpdateManyAsync(ExpiredOtp(now), Terminal(JobState.Expired, now)
            .Set(x => x.Error, JobDocument.Encode(new JobError("otpExpired"))), cancellationToken: ct);
        await database.Jobs.UpdateManyAsync(Filter.Eq(x => x.State, JobState.Running) & Filter.Lte(x => x.LeaseUntil, now),
            Terminal(JobState.Interrupted, now).Set(x => x.Error, JobDocument.Encode(new JobError("serverInterrupted"))),
            cancellationToken: ct);
    }

    internal string Seal<T>(Guid id, T value) => secrets.Seal(Scope(id), value);
    internal T Read<T>(ClaimedJob job) => secrets.Open<T>(Scope(job.Id), job.Payload);

    private async Task UpdateOwnedAsync(ClaimedJob job, UpdateDefinition<JobDocument> update, DateTime now, CancellationToken ct)
    {
        var updated = await database.Jobs.UpdateOneAsync(Running(job, now), update, cancellationToken: ct);
        if (updated.MatchedCount != 1)
            throw new JobLeaseLostException();
    }

    private static FilterDefinition<JobDocument> Owned(string owner, Guid id) =>
        Filter.Eq(x => x.Id, id.ToString()) & Filter.Eq(x => x.Owner, owner);

    private static FilterDefinition<JobDocument> Running(ClaimedJob job, DateTime now) =>
        Filter.Eq(x => x.Id, job.Id.ToString()) & Filter.Eq(x => x.State, JobState.Running) &
        Filter.Eq(x => x.LeaseId, job.LeaseId) & Filter.Gt(x => x.LeaseUntil, now);

    private static FilterDefinition<JobDocument> ExpiredOtp(DateTime now) =>
        Filter.In(x => x.State, new[] { JobState.AwaitingOtp, JobState.Queued }) & Filter.Lte(x => x.OtpExpiresAt, now);

    private static UpdateDefinition<JobDocument> Terminal(JobState state, DateTime now) =>
        Update.Set(x => x.State, state).Set(x => x.Active, false).Set(x => x.UpdatedAt, now)
            .Unset(x => x.Payload).Unset(x => x.OtpExpiresAt).Unset(x => x.LeaseUntil).Unset(x => x.LeaseId);

    private static string Scope(Guid id) => $"job:{id}";
}
