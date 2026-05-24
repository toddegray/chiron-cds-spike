using Chiron.Cds.Engine;
using Chiron.Cds.Engine.Primitives;
using Microsoft.Data.Sqlite;

namespace Chiron.Cds.Web.Persistence;

/// <summary>
/// SQLite-backed <see cref="IOverrideLog"/>. Durable across process
/// restarts — production would use Postgres but SQLite is sufficient for
/// single-instance deploys, demos, and CI. Schema is created lazily on
/// first use; queries are aggregated into a single fingerprint summary
/// (fires + overrides) so the fatigue report scales with the number of
/// distinct alert fingerprints rather than the number of fire events.
/// </summary>
public sealed class SqliteOverrideLog : IOverrideLog, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(initialCount: 1, maxCount: 1);
    // Keepalive connection: required when the connection string targets an
    // in-memory shared-cache database — the cache is destroyed when the
    // last connection closes. For a file-backed connection string it's
    // an idle pooled connection that costs nothing.
    private readonly SqliteConnection _keepAlive;

    public SqliteOverrideLog(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS override_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fingerprint TEXT NOT NULL,
                rule_id TEXT NOT NULL,
                event_kind TEXT NOT NULL,   -- 'fire' or 'override'
                overridden_by TEXT NULL,
                reason TEXT NULL,
                at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_override_log_fp ON override_log(fingerprint);";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void RecordFire(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        _writeLock.Wait();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO override_log
                (fingerprint, rule_id, event_kind, at_utc)
                VALUES ($fp, $rid, 'fire', $at);";
            cmd.Parameters.AddWithValue("$fp", alert.Fingerprint);
            cmd.Parameters.AddWithValue("$rid", alert.RuleId);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public void RecordOverride(string fingerprint, string overriddenBy, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(overriddenBy);
        _writeLock.Wait();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO override_log
                (fingerprint, rule_id, event_kind, overridden_by, reason, at_utc)
                VALUES ($fp, $rid, 'override', $by, $reason, $at);";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$rid", "<unknown>");
            cmd.Parameters.AddWithValue("$by", overriddenBy);
            cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public IReadOnlyList<FatigueRow> FatigueReport()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                fingerprint,
                COALESCE(MAX(CASE WHEN event_kind = 'fire' THEN rule_id END), '<unknown>') AS rule_id,
                SUM(CASE WHEN event_kind = 'fire' THEN 1 ELSE 0 END) AS fires,
                SUM(CASE WHEN event_kind = 'override' THEN 1 ELSE 0 END) AS overrides
            FROM override_log
            GROUP BY fingerprint;";
        using var reader = cmd.ExecuteReader();
        var rows = new List<FatigueRow>();
        while (reader.Read())
        {
            var fingerprint = reader.GetString(0);
            var ruleId = reader.GetString(1);
            var fires = reader.GetInt32(2);
            var overrides = reader.GetInt32(3);
            rows.Add(new FatigueRow(
                Fingerprint: fingerprint,
                RuleId: ruleId,
                Fires: fires,
                Overrides: overrides,
                OverrideRate: fires == 0 ? 0 : (double)overrides / fires));
        }
        return rows
            .OrderByDescending(r => r.OverrideRate)
            .ThenBy(r => r.Fingerprint, StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _keepAlive.Dispose();
    }
}
