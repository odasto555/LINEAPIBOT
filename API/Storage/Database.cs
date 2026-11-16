using LineJobs.Configuration;
using LineJobs.Jobs;
using LineJobs.Security;
using MongoDB.Bson;
using MongoDB.Driver;

namespace LineJobs.Storage;

internal sealed class Database : IDisposable
{
    private static readonly FilterDefinition<QueueControl> Queue = Builders<QueueControl>.Filter.Eq(x => x.Id, "admission");
    private static readonly TransactionOptions Transaction = new(
        readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary, writeConcern: WriteConcern.WMajority);

    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<QueueControl> _queueControl;

    public Database(ApiSettings settings)
    {
        var clientSettings = ReadClientSettings(settings.MongoConnectionString);
        clientSettings.ApplicationName = "LineJobs";
        clientSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        clientSettings.ConnectTimeout = TimeSpan.FromSeconds(5);
        _client = new MongoClient(clientSettings);
        _db = _client.GetDatabase(settings.MongoDatabaseName).WithWriteConcern(WriteConcern.WMajority);
        Jobs = _db.GetCollection<JobDocument>("jobs");
        LineLogs = _db.GetCollection<LineLogDocument>("job_line_logs");
        Accounts = _db.GetCollection<AccountDocument>("accounts");
        Users = _db.GetCollection<UserDocument>("users");
        Sessions = _db.GetCollection<SessionDocument>("sessions");
        _queueControl = _db.GetCollection<QueueControl>("queue_control");
    }

    internal IMongoCollection<JobDocument> Jobs { get; }
    internal IMongoCollection<LineLogDocument> LineLogs { get; }
    internal IMongoCollection<AccountDocument> Accounts { get; }
    internal IMongoCollection<UserDocument> Users { get; }
    internal IMongoCollection<SessionDocument> Sessions { get; }

    internal async Task InitializeAsync(CancellationToken ct)
    {
        var keys = Builders<JobDocument>.IndexKeys;
        var filters = Builders<JobDocument>.Filter;
        await Jobs.Indexes.CreateManyAsync(
        [
            new(keys.Ascending(x => x.ResourceKey), new CreateIndexOptions<JobDocument>
            {
                Name = "jobs_active_resource",
                Unique = true,
                PartialFilterExpression = filters.Eq(x => x.Active, true)
            }),
            new(keys.Ascending(x => x.Owner).Ascending(x => x.RequestId), new CreateIndexOptions<JobDocument>
            {
                Name = "jobs_request_id",
                Unique = true,
                PartialFilterExpression = filters.Exists(x => x.RequestId)
            }),
            new(keys.Ascending(x => x.State).Ascending(x => x.UpdatedAt).Ascending(x => x.Id)),
            new(keys.Ascending(x => x.State).Ascending(x => x.OtpExpiresAt)),
            new(keys.Ascending(x => x.State).Ascending(x => x.LeaseUntil)),
            new(keys.Ascending(x => x.Owner).Descending(x => x.CreatedAt).Descending(x => x.Id)),
            new(keys.Ascending(x => x.Active).Ascending(x => x.Owner)),
            new(keys.Descending(x => x.UpdatedAt).Descending(x => x.Id)),
            new(keys.Ascending(x => x.Active).Descending(x => x.UpdatedAt).Descending(x => x.Id))
        ], cancellationToken: ct);
        await Accounts.Indexes.CreateOneAsync(new CreateIndexModel<AccountDocument>(
            Builders<AccountDocument>.IndexKeys.Ascending(x => x.Owner).Ascending(x => x.Id)), cancellationToken: ct);
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<UserDocument>(
            Builders<UserDocument>.IndexKeys.Ascending(x => x.NormalizedUsername),
            new CreateIndexOptions { Name = "users_username_unique", Unique = true }), cancellationToken: ct);
        await Sessions.Indexes.CreateOneAsync(new CreateIndexModel<SessionDocument>(
            Builders<SessionDocument>.IndexKeys.Ascending(x => x.ExpiresAt),
            new CreateIndexOptions { Name = "sessions_expiration", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await _queueControl.UpdateOneAsync(Queue, Builders<QueueControl>.Update.SetOnInsert(x => x.Version, 0L),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    internal async Task<T> WithAdmissionLockAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (transaction, transactionToken) =>
        {
            await _queueControl.UpdateOneAsync(transaction, Queue,
                Builders<QueueControl>.Update.Inc(x => x.Version, 1L), cancellationToken: transactionToken);
            return await action(transaction, transactionToken);
        }, Transaction, ct);
    }

    internal Task PingAsync(CancellationToken ct) =>
        _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);

    private static MongoClientSettings ReadClientSettings(string connectionString)
    {
        try
        {
            return MongoClientSettings.FromConnectionString(connectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or MongoConfigurationException)
        {
            throw new InvalidOperationException("MongoDb:ConnectionString is invalid.");
        }
    }

    public void Dispose() => _client.Dispose();
}

internal sealed class DatabaseStartup(Database database, UserSessions sessions) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await database.InitializeAsync(deadline.Token);
            await sessions.BootstrapAsync(deadline.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is MongoException or TimeoutException or OperationCanceledException)
        {
            throw new InvalidOperationException("MongoDB initialization failed. Check connection, database access and indexes.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
