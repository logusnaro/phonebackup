using System.IO;
using System.Text.Json;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchVerificationResult(
    Guid BackupId,
    bool Success,
    int RecordingFiles,
    int ExpectedRecordingFiles,
    IReadOnlyList<string> Issues);

/// <summary>
/// Controls the supported part of Smart Switch integration: launching the PC
/// application and verifying a completed backup package. Smart Switch does not
/// publish a stable command-line backup API, so starting the backup itself
/// remains a manual fallback when the vendor UI does not accept automation.
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
        var rows = await _database.QueryAsync(
            "SELECT id FROM devices WHERE member_id=$member AND platform='SmartSwitch' AND model=$model ORDER BY last_seen_at DESC LIMIT 1",
            r => Guid.Parse(r.GetString(0)),
            p => { p.AddWithValue("$member", memberId.ToString()); p.AddWithValue("$model", model); });
        return rows.FirstOrDefault();
    }

    public async Task<SmartSwitchVerificationResult> VerifyAsync(Guid deviceId, string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Smart Switch 폴더를 찾을 수 없습니다: {sourceRoot}");
        var startedAt = DateTimeOffset.UtcNow;
        var backupId = Guid.NewGuid();
        await _database.ExecuteAsync("""
            INSERT INTO smart_switch_backups(id,device_id,source_path,started_at,status)
            VALUES($id,$device,$path,$started,$status)
            """, p =>
        {
            p.AddWithValue("$id", backupId.ToString());
            p.AddWithValue("$device", deviceId.ToString());
            p.AddWithValue("$path", sourceRoot);
            p.AddWithValue("$started", startedAt.ToString("O"));
            p.AddWithValue("$status", (int)SyncRunStatus.Running);
        });

        var issues = new List<string>();
        var recordingFiles = Directory.EnumerateFiles(sourceRoot, "*.m4a", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("TPhoneCallRecords", StringComparison.OrdinalIgnoreCase)))
            .Select(path => new FileInfo(path))
            .ToList();
        var verifiedRecordingFiles = recordingFiles.Count(file => file.Length > 0);
        if (recordingFiles.Any(file => file.Length == 0)) issues.Add("비어 있는 통화녹음 파일이 있습니다.");

        var expectedRecordingFiles = 0;
        var requestInfoPath = Directory.EnumerateFiles(sourceRoot, "ReqItemsInfo.json", SearchOption.AllDirectories).FirstOrDefault();
        if (requestInfoPath is null) issues.Add("ReqItemsInfo.json을 찾지 못했습니다.");
        else
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(requestInfoPath));
                if (document.RootElement.TryGetProperty("ListItems", out var listItems))
                {
                    foreach (var item in listItems.EnumerateArray())
                    {
                        if (item.TryGetProperty("Type", out var type) && string.Equals(type.GetString(), "MUSIC", StringComparison.OrdinalIgnoreCase))
                            expectedRecordingFiles = item.TryGetProperty("ViewCount", out var count) ? count.GetInt32() : 0;
                    }
                }
            }
            catch (Exception ex) { issues.Add($"Smart Switch 항목 정보 해석 실패: {ex.Message}"); }
        }
        if (expectedRecordingFiles > 0 && expectedRecordingFiles != recordingFiles.Count)
            issues.Add($"통화녹음 개수 불일치: 메타데이터 {expectedRecordingFiles}개 / 실제 {recordingFiles.Count}개");

        var backupInfoPath = Directory.EnumerateFiles(sourceRoot, "SmartSwitchBackup_back.json", SearchOption.AllDirectories).FirstOrDefault();
        if (backupInfoPath is null) issues.Add("SmartSwitchBackup_back.json을 찾지 못했습니다.");
        else
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(backupInfoPath));
                if (document.RootElement.TryGetProperty("IsApp", out var apps))
                {
                    foreach (var app in apps.EnumerateArray())
                    {
                        var type = app.TryGetProperty("Type", out var typeValue) ? typeValue.GetString() : "알 수 없는 항목";
                        if (!app.TryGetProperty("ContentBnrResult", out var result) ||
                            !result.TryGetProperty("Result", out var success) || !success.GetBoolean())
                        {
                            // Smart Switch reports an empty playlist as a false
                            // result even though it contains no user file.
                            if (string.Equals(type, "PLAYLIST", StringComparison.OrdinalIgnoreCase)) continue;
                            issues.Add($"Smart Switch 항목 검증 실패: {type}");
                        }
                    }
                }
            }
            catch (Exception ex) { issues.Add($"Smart Switch 결과 해석 실패: {ex.Message}"); }
        }

        var successResult = issues.Count == 0 && recordingFiles.Count > 0 &&
            (expectedRecordingFiles == 0 || expectedRecordingFiles == recordingFiles.Count);
        var completedAt = DateTimeOffset.UtcNow;
        await _database.ExecuteAsync("""
            UPDATE smart_switch_backups
            SET completed_at=$completed,status=$status,verified_at=$verified,
                files_total=$total,files_verified=$verifiedFiles,error=$error
            WHERE id=$id
            """, p =>
        {
            p.AddWithValue("$completed", completedAt.ToString("O"));
            p.AddWithValue("$status", (int)(successResult ? SyncRunStatus.Completed : SyncRunStatus.Partial));
            p.AddWithValue("$verified", successResult ? completedAt.ToString("O") : DBNull.Value);
            p.AddWithValue("$total", recordingFiles.Count);
            p.AddWithValue("$verifiedFiles", verifiedRecordingFiles);
            p.AddWithValue("$error", issues.Count == 0 ? DBNull.Value : string.Join("; ", issues));
            p.AddWithValue("$id", backupId.ToString());
        });
        return new SmartSwitchVerificationResult(backupId, successResult, recordingFiles.Count, expectedRecordingFiles, issues);
    }
}
