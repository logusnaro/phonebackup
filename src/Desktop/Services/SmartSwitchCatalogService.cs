using System.IO;
using System.Security.Cryptography;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed record CatalogProgress(int Processed, int Total, string CurrentFile, string Stage);
public sealed record CatalogResult(Guid SourceId, int TotalFiles, long TotalBytes, int Added, int Updated, int Missing);
public sealed record VerificationResult(int Total, int Verified, int Changed, int Missing);

public sealed class SmartSwitchCatalogService
{
    private readonly DatabaseService _database;

    public SmartSwitchCatalogService(DatabaseService database) => _database = database;

    public async Task<CatalogResult> IndexAsync(string sourceRoot,
        IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var sourceId = await EnsureSourceAsync(sourceRoot);
        var files = SmartSwitchFileClassifier.Enumerate(sourceRoot)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var known = (await _database.QueryAsync(
                "SELECT relative_path,size_bytes,last_modified_at FROM managed_files WHERE source_id=$source",
                reader => new
                {
                    Path = reader.GetString(0), Size = reader.GetInt64(1), Modified = reader.GetString(2)
                }, parameters => parameters.AddWithValue("$source", sourceId.ToString())))
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var updated = 0;
        long totalBytes = 0;
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new CatalogProgress(index, files.Count, file.OriginalFileName, "파일 분류 중"));
            seen.Add(file.RelativePath);
            totalBytes += file.SizeBytes;
            var changed = !known.TryGetValue(file.RelativePath, out var old) ||
                          old.Size != file.SizeBytes || old.Modified != file.LastModifiedAt.ToString("O");
            if (!known.ContainsKey(file.RelativePath)) added++;
            else if (changed) updated++;

            var metadata = file.Category == "recording"
                ? RecordingMetadataParser.Parse(file.FullPath, file.LastModifiedAt)
                : new RecordingMetadata(null, null, null, null, null);
            await _database.ExecuteAsync("""
                INSERT INTO managed_files(id,source_id,relative_path,full_path,original_file_name,category,
                  size_bytes,last_modified_at,recorded_at,parsed_phone_number,parsed_contact_name,
                  parsed_target,parsed_affiliation,sha256,verified_at,state,last_indexed_at)
                VALUES($id,$source,$relative,$full,$name,$category,$size,$modified,$recorded,$phone,$contact,
                  $target,$affiliation,NULL,NULL,0,$indexed)
                ON CONFLICT(source_id,relative_path) DO UPDATE SET
                  full_path=excluded.full_path,original_file_name=excluded.original_file_name,
                  category=excluded.category,size_bytes=excluded.size_bytes,last_modified_at=excluded.last_modified_at,
                  recorded_at=excluded.recorded_at,parsed_phone_number=excluded.parsed_phone_number,
                  parsed_contact_name=excluded.parsed_contact_name,parsed_target=excluded.parsed_target,
                  parsed_affiliation=excluded.parsed_affiliation,
                  sha256=CASE WHEN managed_files.size_bytes<>excluded.size_bytes OR managed_files.last_modified_at<>excluded.last_modified_at THEN NULL ELSE managed_files.sha256 END,
                  verified_at=CASE WHEN managed_files.size_bytes<>excluded.size_bytes OR managed_files.last_modified_at<>excluded.last_modified_at THEN NULL ELSE managed_files.verified_at END,
                  state=0,last_indexed_at=excluded.last_indexed_at
                """, parameters =>
            {
                parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                parameters.AddWithValue("$source", sourceId.ToString());
                parameters.AddWithValue("$relative", file.RelativePath);
                parameters.AddWithValue("$full", file.FullPath);
                parameters.AddWithValue("$name", file.OriginalFileName);
                parameters.AddWithValue("$category", file.Category);
                parameters.AddWithValue("$size", file.SizeBytes);
                parameters.AddWithValue("$modified", file.LastModifiedAt.ToString("O"));
                parameters.AddWithValue("$recorded", metadata.RecordedAt?.ToString("O") ?? (object)DBNull.Value);
                parameters.AddWithValue("$phone", metadata.PhoneNumber ?? (object)DBNull.Value);
                parameters.AddWithValue("$contact", metadata.ContactName ?? (object)DBNull.Value);
                parameters.AddWithValue("$target", metadata.Target ?? (object)DBNull.Value);
                parameters.AddWithValue("$affiliation", metadata.Affiliation ?? (object)DBNull.Value);
                parameters.AddWithValue("$indexed", DateTimeOffset.UtcNow.ToString("O"));
            });
            progress?.Report(new CatalogProgress(index + 1, files.Count, file.OriginalFileName, "파일 분류 중"));
        }

        var missing = 0;
        foreach (var path in known.Keys.Where(path => !seen.Contains(path)))
        {
            missing++;
            await _database.ExecuteAsync(
                "UPDATE managed_files SET state=2,last_indexed_at=$at WHERE source_id=$source AND relative_path=$path",
                parameters =>
                {
                    parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                    parameters.AddWithValue("$source", sourceId.ToString());
                    parameters.AddWithValue("$path", path);
                });
        }
        await _database.ExecuteAsync("""
            UPDATE managed_sources SET model=$model,last_scan_at=$at,files_count=$count,total_bytes=$bytes,
              status=$status WHERE id=$id
            """, parameters =>
        {
            parameters.AddWithValue("$model", SmartSwitchFileClassifier.FindModel(sourceRoot));
            parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            parameters.AddWithValue("$count", files.Count);
            parameters.AddWithValue("$bytes", totalBytes);
            parameters.AddWithValue("$status", files.Count > 0 ? 0 : 1);
            parameters.AddWithValue("$id", sourceId.ToString());
        });
        return new CatalogResult(sourceId, files.Count, totalBytes, added, updated, missing);
    }

    public async Task<VerificationResult> VerifyAsync(Guid sourceId,
        IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = await _database.QueryAsync(
            "SELECT id,full_path,size_bytes,last_modified_at,original_file_name FROM managed_files WHERE source_id=$source AND state<>2 ORDER BY relative_path",
            reader => new { Id = reader.GetString(0), Path = reader.GetString(1), Size = reader.GetInt64(2), Modified = reader.GetString(3), Name = reader.GetString(4) },
            parameters => parameters.AddWithValue("$source", sourceId.ToString()));
        var verified = 0;
        var changed = 0;
        var missing = 0;
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new CatalogProgress(index, files.Count, file.Name, "무결성 확인 중"));
            if (!File.Exists(file.Path))
            {
                missing++;
                await SetVerificationAsync(file.Id, null, null, 2);
                continue;
            }
            var info = new FileInfo(file.Path);
            if (info.Length != file.Size || new DateTimeOffset(info.LastWriteTimeUtc).ToString("O") != file.Modified)
            {
                changed++;
                await SetVerificationAsync(file.Id, null, null, 1);
                continue;
            }
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, true);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            await SetVerificationAsync(file.Id, sha, DateTimeOffset.UtcNow, 0);
            verified++;
            progress?.Report(new CatalogProgress(index + 1, files.Count, file.Name, "무결성 확인 중"));
        }
        return new VerificationResult(files.Count, verified, changed, missing);
    }

    public Task RemoveSourceAsync(Guid sourceId) => _database.ExecuteAsync(
        "DELETE FROM managed_files WHERE source_id=$id; DELETE FROM managed_sources WHERE id=$id;",
        parameters => parameters.AddWithValue("$id", sourceId.ToString()));

    private async Task<Guid> EnsureSourceAsync(string root)
    {
        var existing = await _database.QueryAsync("SELECT id FROM managed_sources WHERE root_path=$path",
            reader => Guid.Parse(reader.GetString(0)), parameters => parameters.AddWithValue("$path", root));
        if (existing.Count > 0) return existing[0];
        var id = Guid.NewGuid();
        await _database.ExecuteAsync("""
            INSERT INTO managed_sources(id,root_path,display_name,model,created_at,status)
            VALUES($id,$path,$name,$model,$at,0)
            """, parameters =>
        {
            parameters.AddWithValue("$id", id.ToString());
            parameters.AddWithValue("$path", root);
            parameters.AddWithValue("$name", Path.GetFileName(root));
            parameters.AddWithValue("$model", SmartSwitchFileClassifier.FindModel(root));
            parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        });
        return id;
    }

    private Task SetVerificationAsync(string id, string? sha, DateTimeOffset? verifiedAt, int state) =>
        _database.ExecuteAsync(
            "UPDATE managed_files SET sha256=$sha,verified_at=$verified,state=$state WHERE id=$id",
            parameters =>
            {
                parameters.AddWithValue("$sha", sha ?? (object)DBNull.Value);
                parameters.AddWithValue("$verified", verifiedAt?.ToString("O") ?? (object)DBNull.Value);
                parameters.AddWithValue("$state", state);
                parameters.AddWithValue("$id", id);
            });
}
