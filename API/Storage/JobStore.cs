using System.Text.Json;
using LineJobs.Configuration;
using LineJobs.Jobs;
using LineJobs.Security;
using Microsoft.Data.Sqlite;

namespace LineJobs.Storage;

internal sealed class JobStore(Database database, ApiSettings settings, SecretStore secrets)
{
    private const string StatusColumns = "id,kind,state,stage,created_at,updated_at,otp_expires_at,result,error";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal EnqueueResult Enqueue(EnqueueRequest job)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (job.RequestId is not null)
        {
            using var lookup = db.Sql("SELECT id,fingerprint FROM jobs WHERE owner=$owner AND request_id=$key;", tx,
                ("$owner", job.Owner), ("$key", job.RequestId));
            using var row = lookup.ExecuteReader();
            if (row.Read())
                return row.GetString(1) == job.Fingerprint
                    ? new EnqueueResult(StoreOutcome.Applied, Guid.Parse(row.GetString(0)))
                    : new EnqueueResult(StoreOutcome.DuplicateRequest, Guid.Empty);
        }

        var now = UtcNow();
        Expire(db, tx, now);
        using (var active = db.Sql("""
            SELECT EXISTS(SELECT 1 FROM jobs WHERE resource_key=$key AND state IN ('Queued','Running','AwaitingOtp'));
            """, tx, ("$key", job.ResourceKey)))
        {
            if ((long)active.ExecuteScalar()! == 1)
                return new EnqueueResult(StoreOutcome.DuplicateAccount, Guid.Empty);
        }

        using (var capacity = db.Sql("""
            SELECT COUNT(*),COUNT(CASE WHEN owner=$owner THEN 1 END)
            FROM jobs WHERE state IN ('Queued','Running','AwaitingOtp');
            """, tx, ("$owner", job.Owner)))
        {
            using var row = capacity.ExecuteReader();
            row.Read();
            if (row.GetInt64(0) >= settings.QueueCapacity || row.GetInt64(1) >= settings.MaxActivePerUser)
                return new EnqueueResult(StoreOutcome.Full, Guid.Empty);
        }

        using var insert = db.Sql("""
            INSERT INTO jobs (id,owner,kind,resource_key,state,stage,payload,created_at,updated_at,request_id,fingerprint)
            VALUES ($id,$owner,$kind,$resource,'Queued',$stage,$payload,$now,$now,$key,$fingerprint);
            """, tx, ("$id", job.Id.ToString()), ("$owner", job.Owner), ("$kind", job.Kind.ToString()),
            ("$resource", job.ResourceKey), ("$stage", job.Stage), ("$payload", job.Payload),
            ("$now", now), ("$key", job.RequestId), ("$fingerprint", job.Fingerprint));
        insert.ExecuteNonQuery();
        tx.Commit();
        return new EnqueueResult(StoreOutcome.Applied, job.Id);
    }

    internal JobStatus? Get(string owner, Guid id)
    {
        using var db = database.Open();
        using var cmd = db.Sql($"SELECT {StatusColumns} FROM jobs WHERE id=$id AND owner=$owner;", null,
            ("$id", id.ToString()), ("$owner", owner));
        using var row = cmd.ExecuteReader();
        return row.Read() ? ReadStatus(row) : null;
    }

    internal IReadOnlyList<JobStatus> List(string owner, int limit)
    {
        using var db = database.Open();
        using var cmd = db.Sql($"SELECT {StatusColumns} FROM jobs WHERE owner=$owner ORDER BY created_at DESC,rowid DESC LIMIT $limit;",
            null, ("$owner", owner), ("$limit", limit));
        using var rows = cmd.ExecuteReader();
        var jobs = new List<JobStatus>();
        while (rows.Read())
            jobs.Add(ReadStatus(rows));
        return jobs;
    }

    internal ClaimedJob? Claim()
    {
        using var db = database.Open();
        using var cmd = db.Sql("""
            UPDATE jobs SET state='Running',updated_at=$now
            WHERE id=(
                SELECT id FROM jobs WHERE state='Queued' AND (otp_expires_at IS NULL OR otp_expires_at>$now)
                ORDER BY updated_at,rowid LIMIT 1
            )
            RETURNING id,owner,kind,payload;
            """, null, ("$now", UtcNow()));
        using var row = cmd.ExecuteReader();
        return row.Read()
            ? new ClaimedJob(Guid.Parse(row.GetString(0)), row.GetString(1),
                Enum.Parse<JobKind>(row.GetString(2)), row.GetString(3))
            : null;
    }

    internal void SetStage(Guid id, string stage)
    {
        using var db = database.Open();
        using var cmd = db.Sql("UPDATE jobs SET stage=$stage,updated_at=$now WHERE id=$id AND state='Running';", null,
            ("$id", id.ToString()), ("$stage", stage), ("$now", UtcNow()));
        EnsureUpdated(cmd);
    }

    internal void AwaitOtp(Guid id, RegistrationWork work)
    {
        using var db = database.Open();
        var now = DateTimeOffset.UtcNow;
        using var cmd = db.Sql("""
            UPDATE jobs SET state='AwaitingOtp',stage='awaitingOtp',payload=$payload,updated_at=$now,otp_expires_at=$expires
            WHERE id=$id AND state='Running';
            """, null, ("$id", id.ToString()), ("$now", now.ToUnixTimeMilliseconds()),
            ("$expires", now.AddMinutes(settings.OtpMinutes).ToUnixTimeMilliseconds()), ("$payload", Seal(id, work)));
        EnsureUpdated(cmd);
    }

    internal StoreOutcome SubmitOtp(string owner, Guid id, string code)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var select = db.Sql("SELECT state,kind,otp_expires_at,payload FROM jobs WHERE id=$id AND owner=$owner;", tx,
            ("$id", id.ToString()), ("$owner", owner));
        string payload;
        using (var row = select.ExecuteReader())
        {
            if (!row.Read())
                return StoreOutcome.NotFound;
            var state = Enum.Parse<JobState>(row.GetString(0));
            if (state == JobState.Expired)
                return StoreOutcome.Expired;
            if (state != JobState.AwaitingOtp || row.GetString(1) != nameof(JobKind.Registration))
                return StoreOutcome.WrongState;
            if (row.GetInt64(2) <= UtcNow())
            {
                row.Close();
                Expire(db, tx, UtcNow());
                tx.Commit();
                return StoreOutcome.Expired;
            }
            var work = secrets.Open<RegistrationWork>(Scope(id), row.GetString(3));
            payload = Seal(id, work with { Otp = code });
        }

        var now = UtcNow();
        using var update = db.Sql("""
            UPDATE jobs SET state='Queued',stage='verifyOtp',payload=$payload,updated_at=$now
            WHERE id=$id AND state='AwaitingOtp' AND otp_expires_at>$now;
            """, tx, ("$id", id.ToString()), ("$payload", payload), ("$now", now));
        var updated = update.ExecuteNonQuery() == 1;
        if (!updated)
            Expire(db, tx, now);
        tx.Commit();
        return updated ? StoreOutcome.Applied : StoreOutcome.Expired;
    }

    internal void Complete(Guid id, JobResult result, AccountWrite? account = null)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (account is not null && !AccountStore.Save(db, tx, account))
            throw new JobRejectedException("accountAlreadyLinked");

        using var cmd = db.Sql("""
            UPDATE jobs SET state='Succeeded',stage='done',updated_at=$now,result=$result,
                payload=NULL,otp_expires_at=NULL WHERE id=$id AND state='Running';
            """, tx, ("$id", id.ToString()), ("$now", UtcNow()), ("$result", JsonSerializer.Serialize(result, Json)));
        EnsureUpdated(cmd);
        tx.Commit();
    }

    internal void Fail(Guid id, JobError error, JobState state = JobState.Failed)
    {
        using var db = database.Open();
        using var cmd = db.Sql("""
            UPDATE jobs SET state=$state,updated_at=$now,error=$error,payload=NULL,otp_expires_at=NULL
            WHERE id=$id AND state='Running';
            """, null, ("$id", id.ToString()), ("$state", state.ToString()), ("$now", UtcNow()),
            ("$error", JsonSerializer.Serialize(error, Json)));
        EnsureUpdated(cmd);
    }

    internal void Expire()
    {
        using var db = database.Open();
        Expire(db, null, UtcNow());
    }

    internal string Seal<T>(Guid id, T value) => secrets.Seal(Scope(id), value);
    internal T Read<T>(ClaimedJob job) => secrets.Open<T>(Scope(job.Id), job.Payload);

    private static void Expire(SqliteConnection db, SqliteTransaction? tx, long now)
    {
        using var cmd = db.Sql("""
            UPDATE jobs SET state='Expired',updated_at=$now,payload=NULL,otp_expires_at=NULL,error='{"code":"otpExpired"}'
            WHERE state IN ('AwaitingOtp','Queued') AND otp_expires_at<=$now;
            """, tx, ("$now", now));
        cmd.ExecuteNonQuery();
    }

    private static void EnsureUpdated(SqliteCommand cmd)
    {
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Job state changed unexpectedly.");
    }

    private static JobStatus ReadStatus(SqliteDataReader row) => new(
        Guid.Parse(row.GetString(0)), Enum.Parse<JobKind>(row.GetString(1)), Enum.Parse<JobState>(row.GetString(2)),
        row.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(4)),
        DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(5)),
        row.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(6)),
        row.Text(7) is { } result ? JsonSerializer.Deserialize<JobResult>(result, Json) : null,
        row.Text(8) is { } error ? JsonSerializer.Deserialize<JobError>(error, Json) : null);

    private static string Scope(Guid id) => $"job:{id}";
    private static long UtcNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
