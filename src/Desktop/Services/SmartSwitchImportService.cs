using System.IO;
using System.Security.Cryptography;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchImportProgress(int Processed, int Total, int Stored, int Failed, string CurrentFile);
public sealed record SmartSwitchImportResult(
    Guid DeviceId,
    string Model,
    bool LinkedToPairedPhone,
    int Seen,
    int Stored,
    int Failed,
    int Skipped,
    IReadOnlyList<string> Errors);

/// <summary>
/// Imports the user-visible files from a Samsung Smart Switch PC backup.
/// Smart Switch's contacts/messages container is proprietary; it is kept
/// untouched and only ordinary media/document files are indexed by PhoneBackup.
/// </summary>
public sealed class SmartSwitchImportService
{
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

        var files = SmartSwitchFileClassifier.Enumerate(sourceRoot)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidDataException("선택한 Smart Switch 폴더에서 가져올 통화녹음·사진·영상·문서 파일을 찾지 못했습니다.");

        var model = SmartSwitchFileClassifier.FindModel(sourceRoot);
        var device = await EnsureImportedDeviceAsync(memberId, model);
        var deviceId = device.DeviceId;
        var syncRunId = await _backups.StartSyncAsync(deviceId);
        var stored = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                progress?.Report(new SmartSwitchImportProgress(index, files.Count, stored, failed, file.OriginalFileName));
                try
                {
                    var sha = await ComputeStableSha256Async(file.FullPath, cancellationToken);
                    if (await _backups.IsKnownAsync(deviceId, file.RelativePath, sha))
                    {
                        skipped++;
                        continue;
                    }

                    var item = file.Category == "recording"
                        ? BackupService.ParseRecording(file.RelativePath, file.SizeBytes, file.LastModifiedAt, sha)
                        : new BackupManifestItem(file.RelativePath, file.OriginalFileName, file.SizeBytes,
                            file.LastModifiedAt, sha, file.Category, null, null, null, null);
                    await using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
                    await _backups.StoreAsync(deviceId, syncRunId, item, input);
                    stored++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Count < 20) errors.Add($"{file.OriginalFileName}: {ex.Message}");
                }
                progress?.Report(new SmartSwitchImportProgress(index + 1, files.Count, stored, failed, file.OriginalFileName));
            }

            var status = failed == 0 ? SyncRunStatus.Completed : SyncRunStatus.Partial;
            await _backups.FinishSyncAsync(syncRunId, status, files.Count, stored,
                failed == 0 ? null : $"{failed}개 파일을 가져오지 못했습니다.");
            return new SmartSwitchImportResult(deviceId, model, device.LinkedToPairedPhone,
                files.Count, stored, failed, skipped, errors);
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

    private async Task<DeviceResolution> EnsureImportedDeviceAsync(Guid memberId, string model)
    {
        if (!model.Equals("모델 미확인", StringComparison.OrdinalIgnoreCase))
        {
            var paired = await _database.QueryAsync(
                "SELECT id FROM devices WHERE member_id=$member AND platform='Android' AND upper(COALESCE(model,''))=$model AND status IN (0,1) ORDER BY status DESC,last_seen_at DESC LIMIT 1",
                r => Guid.Parse(r.GetString(0)),
                p => { p.AddWithValue("$member", memberId.ToString()); p.AddWithValue("$model", model.ToUpperInvariant()); });
            if (paired.Count > 0) return new DeviceResolution(paired[0], true);
        }

        var existing = await _database.QueryAsync(
            "SELECT id FROM devices WHERE member_id=$member AND platform='SmartSwitch' AND model=$model ORDER BY last_seen_at DESC LIMIT 1",
            r => Guid.Parse(r.GetString(0)),
            p => { p.AddWithValue("$member", memberId.ToString()); p.AddWithValue("$model", model); });
        if (existing.Count > 0) return new DeviceResolution(existing[0], false);

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
        return new DeviceResolution(deviceId, false);
    }

    private static async Task<string> ComputeStableSha256Async(string path, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = new FileInfo(path);
                var length = before.Length;
                var modified = before.LastWriteTimeUtc;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                var after = new FileInfo(path);
                if (after.Length != length || after.LastWriteTimeUtc != modified)
                    throw new IOException("가져오는 동안 파일이 변경되었습니다. Smart Switch 백업 종료 후 다시 시도하세요.");
                return hash;
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(350 * (attempt + 1)), cancellationToken);
            }
        }
        throw new IOException("파일을 안정적으로 읽지 못했습니다.", lastError);
    }

    private sealed record DeviceResolution(Guid DeviceId, bool LinkedToPairedPhone);
}
