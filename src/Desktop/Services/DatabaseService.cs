using System.IO;
using Microsoft.Data.Sqlite;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    private SqliteConnection Open() => new(_connectionString);

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS members(
                  id TEXT PRIMARY KEY, name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0,
                  created_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS devices(
                  id TEXT PRIMARY KEY, member_id TEXT NOT NULL REFERENCES members(id),
                  display_name TEXT NOT NULL, platform TEXT NOT NULL, model TEXT,
                  android_version TEXT, status INTEGER NOT NULL DEFAULT 0,
                  last_seen_at TEXT, token_hash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS sync_runs(
                  id TEXT PRIMARY KEY, device_id TEXT NOT NULL REFERENCES devices(id),
                  status INTEGER NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
                  files_seen INTEGER NOT NULL DEFAULT 0, files_stored INTEGER NOT NULL DEFAULT 0,
                  error TEXT);
                CREATE TABLE IF NOT EXISTS sync_progress(
                  sync_run_id TEXT PRIMARY KEY REFERENCES sync_runs(id),
                  files_total INTEGER NOT NULL DEFAULT 0,
                  files_processed INTEGER NOT NULL DEFAULT 0,
                  current_file TEXT, stage TEXT NOT NULL DEFAULT '검색 중',
                  updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS stored_objects(
                  id TEXT PRIMARY KEY, sha256 TEXT NOT NULL UNIQUE, storage_path TEXT NOT NULL,
                  size_bytes INTEGER NOT NULL, created_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS backup_items(
                  id TEXT PRIMARY KEY, stored_object_id TEXT NOT NULL REFERENCES stored_objects(id),
                  device_id TEXT NOT NULL REFERENCES devices(id), relative_path TEXT NOT NULL,
                  original_file_name TEXT NOT NULL, category TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                  last_modified_at TEXT NOT NULL, recorded_at TEXT, duration_seconds INTEGER,
                  parsed_phone_number TEXT, parsed_contact_name TEXT, sha256 TEXT NOT NULL,
                  verified_at TEXT, first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL,
                  UNIQUE(device_id, relative_path, sha256));
                CREATE INDEX IF NOT EXISTS ix_backup_items_contact ON backup_items(parsed_contact_name);
                CREATE INDEX IF NOT EXISTS ix_backup_items_recorded ON backup_items(recorded_at);
                CREATE TABLE IF NOT EXISTS contacts(
                  id TEXT PRIMARY KEY, display_name TEXT NOT NULL, company TEXT, notes TEXT,
                  updated_at TEXT NOT NULL, deleted_at TEXT);
                CREATE TABLE IF NOT EXISTS contact_phones(
                  contact_id TEXT NOT NULL REFERENCES contacts(id), value TEXT NOT NULL,
                  normalized TEXT NOT NULL, PRIMARY KEY(contact_id, normalized));
                CREATE TABLE IF NOT EXISTS contact_emails(
                  contact_id TEXT NOT NULL REFERENCES contacts(id), value TEXT NOT NULL,
                  normalized TEXT NOT NULL, PRIMARY KEY(contact_id, normalized));
                CREATE TABLE IF NOT EXISTS contact_proposals(
                  id TEXT PRIMARY KEY, device_id TEXT NOT NULL REFERENCES devices(id),
                  payload_json TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS pairing_tickets(
                  id TEXT PRIMARY KEY, member_id TEXT NOT NULL REFERENCES members(id),
                  device_token_hash TEXT NOT NULL, expires_at TEXT NOT NULL, used_at TEXT);
                CREATE TABLE IF NOT EXISTS deletion_candidates(
                  id TEXT PRIMARY KEY, device_id TEXT NOT NULL REFERENCES devices(id),
                  relative_path TEXT NOT NULL, sha256 TEXT NOT NULL, verified_at TEXT NOT NULL,
                  eligible_at TEXT NOT NULL, approved_at TEXT);
                CREATE TABLE IF NOT EXISTS schedules(
                  id TEXT PRIMARY KEY, device_id TEXT REFERENCES devices(id), category TEXT NOT NULL,
                  enabled INTEGER NOT NULL DEFAULT 0, weekdays_json TEXT NOT NULL, times_json TEXT NOT NULL,
                  last_occurrence TEXT);
                CREATE TABLE IF NOT EXISTS audit_log(
                  id INTEGER PRIMARY KEY AUTOINCREMENT, event_type TEXT NOT NULL,
                  subject_id TEXT, details_json TEXT, created_at TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task ExecuteAsync(string sql, Action<SqliteParameterCollection>? parameters = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            parameters?.Invoke(command.Parameters);
            await command.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map,
        Action<SqliteParameterCollection>? parameters = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = Open();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            parameters?.Invoke(command.Parameters);
            await using var reader = await command.ExecuteReaderAsync();
            var values = new List<T>();
            while (await reader.ReadAsync()) values.Add(map(reader));
            return values;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
