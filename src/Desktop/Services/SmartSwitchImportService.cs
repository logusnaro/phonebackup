using System.IO;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchImportProgress(int Processed, int Total, int Stored, int Failed, string CurrentFile);
public sealed record SmartSwitchImportResult(Guid DeviceId, int Seen, int Stored, int Failed, int Skipped);

/// <summary>
/// Imports the user-visible files from a Samsung Smart Switch PC backup.
/// Smart Switch's contacts/messages container is proprietary; it is kept
/// untouched and only ordinary media/document files are indexed by PhoneBackup.
/// </summary>
public sealed class SmartSwitchImportService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".amr", ".3ga", ".3gp", ".wav", ".mp3", ".aac", ".ogg", ".flac",
        ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp", ".bmp",
        ".mp4", ".mov", ".avi", ".mkv", ".webm",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".hwp", ".txt", ".csv"
    };

    private static readonly HashSet<string> SmartSwitchPrivateFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AccountsIcons", "CATEGORY_ICON", "CONTACT", "CONTACTSETTING", "MESSAGE", "MESSAGESETTING",
        "CALLLOG", "CALLOGSETTING", "OtgBackupTemp"
    };

    private readonly DatabaseService _database;
    private readonly BackupService _backups;

    public SmartSwitchImportService(DatabaseService database, BackupService backups)
    {
        _database = database;
        _backups = backups;
    }

    public async Task<SmartSwitchImportResult> ImportAsync(
        Guid memberId,
        string sourceRoot,
        IProgress<SmartSwitchImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Smart Switch 폴더를 찾을 수 없습니다: {sourceRoot}");

        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => TryGetCategory(sourceRoot, file.FullName, out _))
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidDataException("선택한 Smart Switch 폴더에서 가져올 통화녹음·사진·영상·문서 파일을 찾지 못했습니다.");

        var model = FindModel(sourceRoot);
        var deviceId = await EnsureImportedDeviceAsync(memberId, model);
        var syncRunId = await _backups.StartSyncAsync(deviceId);
        var stored = 0;
        var failed = 0;
        var skipped = 0;

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var relative = Path.GetRelativePath(sourceRoot, file.FullName).Replace('\\', '/');
                progress?.Report(new SmartSwitchImportProgress(index, files.Count, stored, failed, file.Name));
                try
                {
                    var sha = await BackupService.ComputeSha256Async(file.FullName);
                    if (await _backups.IsKnownAsync(deviceId, relative, sha))
                    {
                        skipped++;
                        continue;
                    }

                    if (!TryGetCategory(sourceRoot, file.FullName, out var category))
                    {
                        skipped++;
                        continue;
                    }

                    var modified = new DateTimeOffset(file.LastWriteTimeUtc);
                    var item = category == "recording"
                        ? BackupService.ParseRecording(relative, file.Length, modified, sha)
                        : new BackupManifestItem(relative, file.Name, file.Length, modified, sha, category, null, null, null, null);
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
                    await _backups.StoreAsync(deviceId, syncRunId, item, input);
                    stored++;
                }
                catch (Exception) when (file.Exists)
                {
                    // A partially synced Smart Switch file must not abort the
                    // rest of the import. It will be reported in the result.
                    failed++;
                }
                progress?.Report(new SmartSwitchImportProgress(index + 1, files.Count, stored, failed, file.Name));
            }

            var status = failed == 0 ? SyncRunStatus.Completed : SyncRunStatus.Partial;
            await _backups.FinishSyncAsync(syncRunId, status, files.Count, stored,
                failed == 0 ? null : $"{failed}개 파일을 가져오지 못했습니다.");
            return new SmartSwitchImportResult(deviceId, files.Count, stored, failed, skipped);
        }
        catch (OperationCanceledException)
        {
            await _backups.FinishSyncAsync(syncRunId, SyncRunStatus.Skipped, 0, stored, "사용자가 가져오기를 취소했습니다.");
            throw;
        }
        catch (Exception ex)
        {
            await _backups.FinishSyncAsync(syncRunId, SyncRunStatus.Failed, 0, stored, ex.Message);
            throw;
        }
    }

    private async Task<Guid> EnsureImportedDeviceAsync(Guid memberId, string model)
    {
        var existing = await _database.QueryAsync(
            "SELECT id FROM devices WHERE member_id=$member AND platform='SmartSwitch' AND model=$model ORDER BY last_seen_at DESC LIMIT 1",
            r => Guid.Parse(r.GetString(0)),
            p => { p.AddWithValue("$member", memberId.ToString()); p.AddWithValue("$model", model); });
        if (existing.Count > 0) return existing[0];

        var deviceId = Guid.NewGuid();
        var tokenHash = PairingService.Hash($"smart-switch-import:{deviceId:N}");
        await _database.ExecuteAsync("""
            INSERT INTO devices(id,member_id,display_name,platform,model,android_version,status,last_seen_at,token_hash)
            VALUES($id,$member,$name,'SmartSwitch',$model,NULL,$status,$seen,$token)
            ;
            INSERT INTO audit_log(event_type,subject_id,details_json,created_at)
            VALUES('smartswitch_import_device_created',$id,$details,$seen)
            """, p =>
        {
            p.AddWithValue("$id", deviceId.ToString());
            p.AddWithValue("$member", memberId.ToString());
            p.AddWithValue("$name", $"Smart Switch 가져오기 ({model})");
            p.AddWithValue("$model", model);
            p.AddWithValue("$status", (int)DeviceStatus.Retired);
            p.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
            p.AddWithValue("$token", tokenHash);
            p.AddWithValue("$details", "Smart Switch PC 백업에서 생성된 보관용 기기");
        });
        return deviceId;
    }

    private static bool TryGetCategory(string sourceRoot, string path, out string category)
    {
        category = string.Empty;
        var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => SmartSwitchPrivateFolders.Contains(segment))) return false;

        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension)) return false;
        var isCallRecording = extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            && segments.Any(segment => segment.Equals("TPhoneCallRecords", StringComparison.OrdinalIgnoreCase));
        if (isCallRecording) { category = "recording"; return true; }

        category = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".heic" or ".webp" or ".bmp" => "image",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => "video",
            ".m4a" or ".amr" or ".3ga" or ".3gp" or ".wav" or ".mp3" or ".aac" or ".ogg" or ".flac" => "audio",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".hwp" or ".txt" or ".csv" => "document",
            _ => string.Empty
        };
        return category.Length > 0;
    }

    private static string FindModel(string sourceRoot)
    {
        var rootParts = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var model = rootParts.FirstOrDefault(x => x.StartsWith("SM-", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(model)) return model;

        // Users often select the SmartSwitch or backup root instead of one
        // model folder. Discover a nested SM-* directory for device grouping.
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith("SM-", StringComparison.OrdinalIgnoreCase)) return name;
        }
        return "SmartSwitch";
    }
}
