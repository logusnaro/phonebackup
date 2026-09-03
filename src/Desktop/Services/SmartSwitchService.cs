using System.IO;
using System.Text.Json;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchVerificationProgress(int Processed, int Total, string CurrentFile);

public sealed record SmartSwitchVerificationResult(
    Guid BackupId,
    bool Success,
    int TotalFiles,
    int VerifiedFiles,
    int RecordingFiles,
    int ExpectedRecordingFiles,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> Issues => Errors.Concat(Warnings).ToList();
}

/// <summary>
/// Opens Smart Switch and verifies that every user-visible source file has a
/// byte-identical PB copy. Samsung JSON manifests are treated as supporting
/// information because their names and fields vary between Smart Switch and
/// phone versions; a missing optional manifest never overrides SHA-256 proof.
/// </summary>
public sealed class SmartSwitchService
{
    private readonly DatabaseService _database;

    public SmartSwitchService(DatabaseService database) => _database = database;

    public bool TryOpenApplication(out string message)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Samsung", "Smart Switch PC", "SmartSwitchPC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Samsung", "Smart Switch PC", "SmartSwitchPC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Samsung", "Smart Switch PC", "SmartSwitchPC.exe")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            message = "Smart Switch PC를 찾지 못했습니다. 먼저 Samsung Smart Switch PC를 설치해 주세요.";
            return false;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            message = $"Smart Switch PC를 열었습니다: {path}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Smart Switch PC를 열지 못했습니다: {ex.Message}";
            return false;
        }
    }

    public async Task<Guid?> FindImportedDeviceAsync(Guid memberId, string model)
    {
        if (!model.Equals("모델 미확인", StringComparison.OrdinalIgnoreCase))
        {
            var paired = await _database.QueryAsync(
                "SELECT id FROM devices WHERE member_id=$member AND platform='Android' AND upper(COALESCE(model,''))=$model AND status IN (0,1) ORDER BY status DESC,last_seen_at DESC LIMIT 1",
                r => Guid.Parse(r.GetString(0)),
                p => { p.AddWithValue("$member", memberId.ToString()); p.AddWithValue("$model", model.ToUpperInvariant()); });
            if (paired.Count > 0) return paired[0];
        }

        var rows = await _database.QueryAsync(
            "SELECT id FROM devices WHERE member_id=$member AND platform='SmartSwitch' AND model=$model ORDER BY last_seen_at DESC LIMIT 1",
            r => Guid.Parse(r.GetString(0)),
            p => { p.AddWithValue("$member", memberId.ToString()); p.AddWithValue("$model", model); });
        return rows.FirstOrDefault();
    }

    public async Task<SmartSwitchVerificationResult> VerifyAsync(
        Guid deviceId,
        string sourceRoot,
        IProgress<SmartSwitchVerificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Smart Switch 폴더를 찾을 수 없습니다: {sourceRoot}");
        var startedAt = DateTimeOffset.UtcNow;
        var backupId = Guid.NewGuid();
        await _database.ExecuteAsync("""
            INSERT INTO smart_switch_backups(id,device_id,source_path,started_at,status)
            VALUES($id,$device,$path,$started,$status)
            """, p =>
        {
            p.AddWithValue("$id", backupId.ToString());
            p.AddWithValue("$device", deviceId.ToString());
            p.AddWithValue("$path", Path.GetFullPath(sourceRoot));
            p.AddWithValue("$started", startedAt.ToString("O"));
            p.AddWithValue("$status", (int)SyncRunStatus.Running);
        });

        var errors = new List<string>();
        var warnings = new List<string>();
        var files = SmartSwitchFileClassifier.Enumerate(sourceRoot);
        var recordingFiles = files.Count(file => file.Category == "recording");
        var verifiedFiles = 0;
        if (files.Count == 0)
            errors.Add("검증할 통화녹음·사진·영상·문서 파일을 찾지 못했습니다. 연락처 전용 .spbm 백업은 PB 파일 관리 대상이 아닙니다.");

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new SmartSwitchVerificationProgress(index, files.Count, file.OriginalFileName));
            try
            {
                if (file.SizeBytes <= 0)
                {
                    errors.Add($"비어 있는 파일: {file.OriginalFileName}");
                    continue;
                }
                var sha = await BackupService.ComputeSha256Async(file.FullPath, cancellationToken);
                var rows = await _database.QueryAsync("""
                    SELECT o.storage_path,o.size_bytes
                    FROM backup_items b JOIN stored_objects o ON o.id=b.stored_object_id
                    WHERE b.device_id=$device AND b.relative_path=$path AND b.sha256=$sha
                      AND b.verified_at IS NOT NULL LIMIT 1
                    """, r => new { Path = r.GetString(0), Size = r.GetInt64(1) }, p =>
                {
                    p.AddWithValue("$device", deviceId.ToString());
                    p.AddWithValue("$path", file.RelativePath);
                    p.AddWithValue("$sha", sha);
                });
                if (rows.Count == 0 || !File.Exists(rows[0].Path) || new FileInfo(rows[0].Path).Length != file.SizeBytes)
                {
                    if (errors.Count < 20) errors.Add($"PB 복사본 불일치: {file.OriginalFileName}");
                    continue;
                }
                verifiedFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (errors.Count < 20) errors.Add($"파일 검증 실패: {file.OriginalFileName} ({ex.Message})");
            }
            progress?.Report(new SmartSwitchVerificationProgress(index + 1, files.Count, file.OriginalFileName));
        }

        var expectedRecordingFiles = ReadManifestHints(sourceRoot, warnings);
        if (expectedRecordingFiles > 0 && expectedRecordingFiles != recordingFiles)
            warnings.Add($"Smart Switch 메타데이터와 통화녹음 개수가 다릅니다({expectedRecordingFiles}/{recordingFiles}). 파일별 해시 결과를 우선 적용했습니다.");
        if (recordingFiles == 0)
            warnings.Add("통화녹음이 없어 모바일 90일 삭제 후보는 생성되지 않습니다. 일반 파일 백업 검증에는 영향이 없습니다.");

        var success = files.Count > 0 && verifiedFiles == files.Count && errors.Count == 0;
        var completedAt = DateTimeOffset.UtcNow;
        await _database.ExecuteAsync("""
            UPDATE smart_switch_backups
            SET completed_at=$completed,status=$status,verified_at=$verified,
                files_total=$total,files_verified=$verifiedFiles,error=$error
            WHERE id=$id;
            INSERT INTO audit_log(event_type,subject_id,details_json,created_at)
            VALUES($event,$id,$details,$completed)
            """, p =>
        {
            p.AddWithValue("$completed", completedAt.ToString("O"));
            p.AddWithValue("$status", (int)(success ? SyncRunStatus.Completed : SyncRunStatus.Partial));
            p.AddWithValue("$verified", success ? completedAt.ToString("O") : DBNull.Value);
            p.AddWithValue("$total", files.Count);
            p.AddWithValue("$verifiedFiles", verifiedFiles);
            p.AddWithValue("$error", errors.Count == 0 ? DBNull.Value : string.Join("; ", errors));
            p.AddWithValue("$id", backupId.ToString());
            p.AddWithValue("$event", success ? "smartswitch_verification_completed" : "smartswitch_verification_blocked");
            p.AddWithValue("$details", JsonSerializer.Serialize(new
            {
                totalFiles = files.Count,
                verifiedFiles,
                recordingFiles,
                warnings
            }));
        });
        return new SmartSwitchVerificationResult(backupId, success, files.Count, verifiedFiles,
            recordingFiles, expectedRecordingFiles, errors, warnings);
    }

    private static int ReadManifestHints(string sourceRoot, List<string> warnings)
    {
        var expectedRecordingFiles = 0;
        var requestFiles = SafeFindFiles(sourceRoot, "ReqItemsInfo.json").ToList();
        var resultFiles = SafeFindFiles(sourceRoot, "SmartSwitchBackup*.json").ToList();
        if (requestFiles.Count == 0) warnings.Add("ReqItemsInfo.json 없음(이 Smart Switch 버전에서는 생략될 수 있음)");
        foreach (var requestInfoPath in requestFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(requestInfoPath));
                if (!TryGetPropertyIgnoreCase(document.RootElement, "ListItems", out var listItems) ||
                    listItems.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in listItems.EnumerateArray())
                {
                    if (!TryGetPropertyIgnoreCase(item, "Type", out var typeElement)) continue;
                    var type = typeElement.GetString() ?? string.Empty;
                    if (!type.Contains("CALL", StringComparison.OrdinalIgnoreCase) &&
                        !type.Contains("TPHONE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryGetPropertyIgnoreCase(item, "ViewCount", out var count) && count.TryGetInt32(out var value))
                        expectedRecordingFiles += value;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                warnings.Add($"Smart Switch 항목 정보 해석 생략: {ex.Message}");
            }
        }

        if (resultFiles.Count == 0) warnings.Add("SmartSwitchBackup 결과 JSON 없음(파일별 해시 검증으로 대체)");
        foreach (var backupInfoPath in resultFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(backupInfoPath));
                if (!TryGetPropertyIgnoreCase(document.RootElement, "IsApp", out var apps) ||
                    apps.ValueKind != JsonValueKind.Array) continue;
                foreach (var app in apps.EnumerateArray())
                {
                    var type = TryGetPropertyIgnoreCase(app, "Type", out var typeValue)
                        ? typeValue.GetString() ?? "알 수 없는 항목" : "알 수 없는 항목";
                    if (!TryGetPropertyIgnoreCase(app, "ContentBnrResult", out var result) ||
                        !TryGetPropertyIgnoreCase(result, "Result", out var success) ||
                        success.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || success.GetBoolean()) continue;
                    if (type.Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase)) continue;
                    warnings.Add($"Smart Switch가 ‘{type}’ 결과를 미완료로 기록했지만 PB 파일은 별도로 검증했습니다.");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                warnings.Add($"Smart Switch 결과 정보 해석 생략: {ex.Message}");
            }
        }
        return expectedRecordingFiles;
    }

    private static IEnumerable<string> SafeFindFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
