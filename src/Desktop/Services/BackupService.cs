using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed class BackupService
{
    private readonly DatabaseService _database;
    private string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhoneBackup");

    public BackupService(DatabaseService database) => _database = database;
    public string Root { get => _root; set { _root = value; Directory.CreateDirectory(value); } }

    public async Task<Guid> StartSyncAsync(Guid deviceId)
    {
        var id = Guid.NewGuid();
        await _database.ExecuteAsync("INSERT INTO sync_runs(id,device_id,status,started_at) VALUES($id,$device,$status,$at)",
            p => { p.AddWithValue("$id", id.ToString()); p.AddWithValue("$device", deviceId.ToString());
                p.AddWithValue("$status", (int)SyncRunStatus.Running); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); });
        await UpdateProgressAsync(id, 0, 0, null, "파일 검색 중");
        return id;
    }

    public Task UpdateProgressAsync(Guid syncRunId, int processed, int total, string? currentFile, string stage)
        => _database.ExecuteAsync("""
            INSERT INTO sync_progress(sync_run_id,files_total,files_processed,current_file,stage,updated_at)
            VALUES($id,$total,$processed,$file,$stage,$at)
            ON CONFLICT(sync_run_id) DO UPDATE SET files_total=excluded.files_total,
              files_processed=excluded.files_processed,current_file=excluded.current_file,
              stage=excluded.stage,updated_at=excluded.updated_at;
            """, p => { p.AddWithValue("$id", syncRunId.ToString()); p.AddWithValue("$total", total);
                p.AddWithValue("$processed", processed); p.AddWithValue("$file", (object?)currentFile ?? DBNull.Value);
                p.AddWithValue("$stage", stage); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); });

    public async Task<bool> IsKnownAsync(Guid deviceId, string relativePath, string sha256)
    {
        var rows = await _database.QueryAsync("SELECT 1 FROM backup_items WHERE device_id=$device AND relative_path=$path AND sha256=$sha AND verified_at IS NOT NULL LIMIT 1",
            _ => true, p => { p.AddWithValue("$device", deviceId.ToString()); p.AddWithValue("$path", relativePath); p.AddWithValue("$sha", sha256); });
        return rows.Count > 0;
    }

    public async Task<int> BackfillRecordingMetadataAsync()
    {
        var rows = await _database.QueryAsync("""
            SELECT id,relative_path,size_bytes,last_modified_at,sha256
            FROM backup_items WHERE category='recording'
            """, r => new
        {
            Id = r.GetString(0),
            RelativePath = r.GetString(1),
            Size = r.GetInt64(2),
            Modified = DateTimeOffset.Parse(r.GetString(3)),
            Sha = r.GetString(4)
        });
        foreach (var row in rows)
        {
            var parsed = ParseRecording(row.RelativePath, row.Size, row.Modified, row.Sha);
            await _database.ExecuteAsync("""
                UPDATE backup_items SET recorded_at=$recorded,parsed_phone_number=$phone,
                    parsed_contact_name=COALESCE($contact,parsed_contact_name)
                WHERE id=$id
                """, p =>
            {
                p.AddWithValue("$recorded", (object?)parsed.RecordedAt?.ToString("O") ?? DBNull.Value);
                p.AddWithValue("$phone", (object?)parsed.ParsedPhoneNumber ?? DBNull.Value);
                p.AddWithValue("$contact", (object?)parsed.ParsedContactName ?? DBNull.Value);
                p.AddWithValue("$id", row.Id);
            });
        }
        return rows.Count;
    }

    public async Task<StoredFileResult> StoreAsync(Guid deviceId, Guid syncRunId, BackupManifestItem item, Stream content)
    {
        Directory.CreateDirectory(_root);
        var objectPath = Path.Combine(_root, "objects", item.Sha256[..2], item.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        if (!File.Exists(objectPath))
        {
            var temp = objectPath + ".partial";
            await using (var output = File.Create(temp)) await content.CopyToAsync(output);
            var actual = await ComputeSha256Async(temp);
            if (!actual.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            { File.Delete(temp); throw new InvalidDataException("SHA-256 검증 실패"); }
            File.Move(temp, objectPath, true);
        }
        var memberId = await _database.QueryAsync("SELECT member_id FROM devices WHERE id=$id", r => r.GetString(0),
            p => p.AddWithValue("$id", deviceId.ToString()));
        var memberFolder = memberId.Count == 0 ? deviceId.ToString() : memberId[0];
        var safeRelative = NormalizeRelativePath(item.RelativePath);
        var presentation = Path.Combine(_root, "members", memberFolder, "devices", deviceId.ToString(), safeRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(presentation)!);
        if (!File.Exists(presentation)) File.Copy(objectPath, presentation);
        var objectId = Guid.NewGuid();
        await _database.ExecuteAsync("""
            INSERT INTO stored_objects(id,sha256,storage_path,size_bytes,created_at) VALUES($id,$sha,$path,$size,$at)
            ON CONFLICT(sha256) DO NOTHING;
            INSERT INTO backup_items(id,stored_object_id,device_id,relative_path,original_file_name,category,size_bytes,
              last_modified_at,recorded_at,duration_seconds,parsed_phone_number,parsed_contact_name,sha256,verified_at,first_seen_at,last_seen_at)
            VALUES($item,(SELECT id FROM stored_objects WHERE sha256=$sha),$device,$relative,$name,$category,$size,$modified,$recorded,$duration,$phone,$contact,$sha,$verified,$at,$at)
            ON CONFLICT(device_id,relative_path,sha256) DO UPDATE SET last_seen_at=excluded.last_seen_at,verified_at=excluded.verified_at;
            """, p => { p.AddWithValue("$id", objectId.ToString()); p.AddWithValue("$sha", item.Sha256); p.AddWithValue("$path", objectPath);
                p.AddWithValue("$size", item.SizeBytes); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                p.AddWithValue("$item", Guid.NewGuid().ToString()); p.AddWithValue("$device", deviceId.ToString()); p.AddWithValue("$relative", item.RelativePath);
                p.AddWithValue("$name", item.OriginalFileName); p.AddWithValue("$category", item.Category); p.AddWithValue("$modified", item.LastModifiedAt.ToString("O"));
                p.AddWithValue("$recorded", (object?)item.RecordedAt?.ToString("O") ?? DBNull.Value); p.AddWithValue("$duration", (object?)item.DurationSeconds ?? DBNull.Value);
                p.AddWithValue("$phone", (object?)item.ParsedPhoneNumber ?? DBNull.Value); p.AddWithValue("$contact", (object?)item.ParsedContactName ?? DBNull.Value);
                p.AddWithValue("$verified", DateTimeOffset.UtcNow.ToString("O")); });
        return new StoredFileResult(objectPath, presentation, item.Sha256);
    }

    public async Task FinishSyncAsync(Guid syncRunId, SyncRunStatus status, int seen, int stored, string? error = null)
    {
        await _database.ExecuteAsync("UPDATE sync_runs SET status=$status,finished_at=$at,files_seen=$seen,files_stored=$stored,error=$error WHERE id=$id",
            p => { p.AddWithValue("$status", (int)status); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$seen", seen); p.AddWithValue("$stored", stored); p.AddWithValue("$error", (object?)error ?? DBNull.Value); p.AddWithValue("$id", syncRunId.ToString()); });
        await UpdateProgressAsync(syncRunId, seen, seen, null, error is null ? "완료" : "실패");
    }

    public async Task<Stream> OpenForRestoreAsync(Guid storedObjectId)
    {
        var rows = await _database.QueryAsync("SELECT storage_path FROM stored_objects WHERE id=$id", r => r.GetString(0),
            p => p.AddWithValue("$id", storedObjectId.ToString()));
        if (rows.Count == 0) throw new FileNotFoundException("백업 파일을 찾을 수 없습니다.");
        return File.OpenRead(rows[0]);
    }

    public static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidDataException("상대 경로가 아닙니다.");
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("허용되지 않는 파일 경로입니다.");
        return Path.Combine(parts);
    }

    public static BackupManifestItem ParseRecording(string relativePath, long size, DateTimeOffset modified, string sha256)
    {
        var name = Path.GetFileName(relativePath);
        // Samsung call-recording names currently arrive in three forms:
        //   이름.직책.기관_전화번호_yyyyMMddHHmmss
        //   이름.생년.진료과_전화번호_yyyyMMddHHmmss
        //   전화번호_yyyyMMddHHmmss
        // Keep the original file name untouched; only index the parsed values.
        var stem = Path.GetFileNameWithoutExtension(name);
        var structured = Regex.Match(stem,
            @"^(?<prefix>.+)_(?<phone>(?:\+?82|0)[0-9\- ]{8,16})_(?<stamp>20\d{12})$");
        string? contactName = null;
        string phone;
        DateTimeOffset? recorded = null;
        if (structured.Success)
        {
            phone = structured.Groups["phone"].Value;
            var prefixParts = structured.Groups["prefix"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (prefixParts.Length >= 3) contactName = prefixParts[0];
            if (DateTimeOffset.TryParseExact(structured.Groups["stamp"].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)) recorded = timestamp;
        }
        else
        {
            phone = Regex.Match(name, @"(?<!\d)(?:\+?82|0)\d{8,10}(?!\d)").Value;
            var timestamp = Regex.Match(stem, @"(?<!\d)(?<stamp>20\d{12})(?!\d)");
            if (timestamp.Success && DateTimeOffset.TryParseExact(timestamp.Groups["stamp"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedTimestamp)) recorded = parsedTimestamp;
            else
            {
                var date = Regex.Match(name, @"(?<y>20\d{2})[\-_\.]?(?<m>\d{2})[\-_\.]?(?<d>\d{2})");
                if (date.Success && DateTimeOffset.TryParse($"{date.Groups["y"].Value}-{date.Groups["m"].Value}-{date.Groups["d"].Value}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) recorded = parsed;
            }
        }
        return new BackupManifestItem(relativePath, name, size, modified, sha256, "recording", null, recorded,
            string.IsNullOrEmpty(phone) ? null : PhoneNumberNormalizer.Normalize(phone), contactName);
    }
}

public sealed record StoredFileResult(string ObjectPath, string PresentationPath, string Sha256);
