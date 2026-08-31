using System.IO.Compression;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBackup.Desktop.Services;

public sealed class DiagnosticsService
{
    private static readonly Regex AbsolutePath = new(@"[A-Za-z]:\\[^\r\n""']+", RegexOptions.Compiled);
    private static readonly Regex PhoneNumber = new(@"(?<!\d)(?:\+?82[- .]?)?0\d{1,2}[- .]?\d{3,4}[- .]?\d{4}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Sha256 = new(@"(?i)\b[a-f0-9]{64}\b", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(token|password|secret|certificate(?:sha256)?|authorization)(\s*[:=]\s*)[^\s,;]+", RegexOptions.Compiled);
    private static readonly Regex FileName = new(@"(?i)([^\\/\s]+\.(?:m4a|mp3|wav|jpg|jpeg|png|heic|mp4|mov|pdf|docx?|xlsx?|hwp|txt))", RegexOptions.Compiled);

    private readonly DatabaseService _database;

    public DiagnosticsService(DatabaseService database) => _database = database;

    public async Task ExportAsync(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var report = new
        {
            reportId = Guid.NewGuid().ToString("N"),
            createdAt = DateTimeOffset.UtcNow,
            appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            os = Environment.OSVersion.VersionString,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            recentSyncRuns = await _database.QueryAsync("""
                SELECT status,started_at,finished_at,files_seen,files_stored,error
                FROM sync_runs ORDER BY started_at DESC LIMIT 10
                """, r => new
                {
                    status = r.GetInt32(0), startedAt = r.GetString(1), finishedAt = r.IsDBNull(2) ? null : r.GetString(2),
                    filesSeen = r.GetInt32(3), filesStored = r.GetInt32(4), error = r.IsDBNull(5) ? null : Redact(r.GetString(5))
                }),
            counts = new
            {
                members = await CountAsync("SELECT COUNT(*) FROM members"),
                devices = await CountAsync("SELECT COUNT(*) FROM devices"),
                backupItems = await CountAsync("SELECT COUNT(*) FROM backup_items"),
                recordings = await CountAsync("SELECT COUNT(*) FROM backup_items WHERE category='recording'"),
                deletionCandidates = await CountAsync("SELECT COUNT(*) FROM deletion_candidates WHERE approved_at IS NULL")
            }
        };

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        AddText(archive, "report.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "PhoneBackup-*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = await File.ReadAllTextAsync(path);
                if (text.Length > 200_000) text = text[^200_000..];
                AddText(archive, $"logs/{Path.GetFileName(path)}", Redact(text));
            }
            catch { /* a concurrently rotating log is optional */ }
        }
    }

    private async Task<int> CountAsync(string sql)
        => (await _database.QueryAsync(sql, r => r.GetInt32(0))).FirstOrDefault();

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static string Redact(string value)
    {
        var result = AbsolutePath.Replace(value, "<redacted-path>");
        result = Secret.Replace(result, "$1$2<redacted-secret>");
        result = Sha256.Replace(result, "<redacted-sha256>");
        result = PhoneNumber.Replace(result, "<redacted-phone>");
        return FileName.Replace(result, "<redacted-file>");
    }
}
