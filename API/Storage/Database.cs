using LineJobs.Configuration;
using Microsoft.Data.Sqlite;

namespace LineJobs.Storage;

internal sealed class Database : IDisposable
{
    private readonly FileStream _processLock;
    private readonly string _connectionString;

    public Database(ApiSettings settings)
    {
        var path = settings.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _processLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS accounts (
                    id TEXT PRIMARY KEY, owner TEXT NOT NULL, secret TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY, owner TEXT NOT NULL, kind TEXT NOT NULL, resource_key TEXT NOT NULL,
                    state TEXT NOT NULL, stage TEXT NOT NULL, payload TEXT, created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL, otp_expires_at INTEGER, result TEXT, error TEXT,
                    request_id TEXT, fingerprint TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS jobs_active_resource ON jobs(resource_key)
                    WHERE state IN ('Queued','Running','AwaitingOtp');
                CREATE UNIQUE INDEX IF NOT EXISTS jobs_request_id ON jobs(owner,request_id) WHERE request_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS jobs_queue ON jobs(state, updated_at);
                CREATE INDEX IF NOT EXISTS jobs_owner ON jobs(owner, created_at DESC);
                """;
            cmd.ExecuteNonQuery();

            using var recovery = db.Sql("""
                UPDATE jobs SET state = 'Interrupted', updated_at = $now, payload = NULL,
                    otp_expires_at = NULL, error = '{"code":"serverInterrupted"}'
                WHERE state = 'Running';
                """, null, ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            recovery.ExecuteNonQuery();
        }
        catch
        {
            _processLock.Dispose();
            throw;
        }
    }

    internal SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose() => _processLock.Dispose();
}

internal static class DatabaseCommands
{
    internal static SqliteCommand Sql(this SqliteConnection db, string sql, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    internal static string? Text(this SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
}
