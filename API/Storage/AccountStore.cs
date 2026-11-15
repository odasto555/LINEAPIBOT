using LineJobs.Jobs;
using LineJobs.Security;
using Microsoft.Data.Sqlite;

namespace LineJobs.Storage;

internal sealed class AccountStore(Database database, SecretStore secrets)
{
    internal bool Exists(string owner, string id)
    {
        using var db = database.Open();
        using var cmd = db.Sql("SELECT 1 FROM accounts WHERE id=$id AND owner=$owner;", null, ("$id", id), ("$owner", owner));
        return cmd.ExecuteScalar() is not null;
    }

    internal AccountSecret Get(string owner, string id)
    {
        using var db = database.Open();
        using var cmd = db.Sql("SELECT secret FROM accounts WHERE id=$id AND owner=$owner;", null, ("$id", id), ("$owner", owner));
        return cmd.ExecuteScalar() is string encrypted
            ? secrets.Open<AccountSecret>(Scope(id, owner), encrypted)
            : throw new JobRejectedException("accountNotFound");
    }

    internal IReadOnlyList<string> List(string owner)
    {
        using var db = database.Open();
        using var cmd = db.Sql("SELECT id FROM accounts WHERE owner=$owner ORDER BY id LIMIT 500;", null, ("$owner", owner));
        using var rows = cmd.ExecuteReader();
        var ids = new List<string>();
        while (rows.Read())
            ids.Add(rows.GetString(0));
        return ids;
    }

    internal StoreOutcome Link(AccountWrite account)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var active = db.Sql("""
            SELECT EXISTS(SELECT 1 FROM jobs WHERE resource_key=$key AND state IN ('Queued','Running','AwaitingOtp'));
            """, tx, ("$key", Resource(account.Id)));
        if ((long)active.ExecuteScalar()! == 1)
            return StoreOutcome.DuplicateAccount;
        if (!Save(db, tx, account))
            return StoreOutcome.DuplicateAccount;

        tx.Commit();
        return StoreOutcome.Applied;
    }

    internal AccountWrite Protect(string id, string owner, AccountSecret secret) =>
        new(id, owner, secrets.Seal(Scope(id, owner), secret));

    internal static string Resource(string id) => $"account:{id}";

    internal static bool Save(SqliteConnection db, SqliteTransaction tx, AccountWrite account)
    {
        using var cmd = db.Sql("""
            INSERT INTO accounts (id,owner,secret) VALUES ($id,$owner,$secret)
            ON CONFLICT(id) DO UPDATE SET secret=excluded.secret WHERE accounts.owner=excluded.owner;
            """, tx, ("$id", account.Id), ("$owner", account.Owner), ("$secret", account.EncryptedSecret));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static string Scope(string id, string owner) => $"account:{owner}:{id}";
}
