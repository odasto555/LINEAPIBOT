using LineJobs.Jobs;
using LineJobs.Security;
using MongoDB.Driver;

namespace LineJobs.Storage;

internal sealed class AccountStore(Database database, SecretStore secrets)
{
    internal async Task<bool> ExistsAsync(string owner, string id, CancellationToken ct) =>
        await database.Accounts.Find(account => account.Owner == owner && account.Id == id).AnyAsync(ct);

    internal async Task<AccountSecret> GetAsync(string owner, string id, CancellationToken ct)
    {
        var account = await database.Accounts.Find(a => a.Id == id && a.Owner == owner).FirstOrDefaultAsync(ct);
        return account is not null
            ? secrets.Open<AccountSecret>(Scope(id, owner), account.Secret)
            : throw new JobRejectedException("accountNotFound");
    }

    internal async Task<IReadOnlyList<string>> ListAsync(string owner, CancellationToken ct) =>
        await database.Accounts.Find(account => account.Owner == owner).SortBy(account => account.Id)
            .Limit(500).Project(account => account.Id).ToListAsync(ct);

    internal Task<StoreOutcome> LinkAsync(AccountWrite account, CancellationToken ct) =>
        database.WithAdmissionLockAsync(async (session, token) =>
        {
            var resource = Resource(account.Id);
            if (await database.Jobs.Find(session, job => job.Active && job.ResourceKey == resource).AnyAsync(token))
                return StoreOutcome.DuplicateAccount;
            return await SaveAsync(session, account, token) ? StoreOutcome.Applied : StoreOutcome.DuplicateAccount;
        }, ct);

    internal static string Resource(string id) => $"account:{id}";

    internal async Task<bool> SaveAsync(IClientSessionHandle session, AccountWrite account, CancellationToken ct)
    {
        var existing = await database.Accounts.Find(session, a => a.Id == account.Id).FirstOrDefaultAsync(ct);
        if (existing is not null && !string.Equals(existing.Owner, account.Owner, StringComparison.Ordinal))
            return false;

        var scope = Scope(account.Id, account.Owner);
        var secret = account.Secret;
        if (secret.Profile is null && existing is not null)
            secret = secret with { Profile = secrets.Open<AccountSecret>(scope, existing.Secret).Profile };

        await database.Accounts.ReplaceOneAsync(session, a => a.Id == account.Id,
            new AccountDocument(account.Id, account.Owner, secrets.Seal(scope, secret)), new ReplaceOptions { IsUpsert = true }, ct);
        return true;
    }

    private static string Scope(string id, string owner) => $"account:{owner}:{id}";
}
