using System.IO;
using System.Text.RegularExpressions;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchSourceFile(string FullPath, string RelativePath, string OriginalFileName,
    string Category, long SizeBytes, DateTimeOffset LastModifiedAt);

/// <summary>Smart Switch 원본을 변경하지 않고 모든 파일을 분류한다.</summary>
public static class SmartSwitchFileClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp", ".bmp", ".tif", ".tiff" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".m4v" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".m4a", ".amr", ".3ga", ".3gp", ".wav", ".mp3", ".aac", ".ogg", ".flac" };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".hwp", ".hwpx", ".cell", ".show", ".txt", ".csv", ".rtf", ".epub" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz" };
    private static readonly HashSet<string> SamsungExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".spbm", ".bk", ".enc", ".dat", ".db", ".sqlite", ".json" };
    private static readonly HashSet<string> SamsungFolders = new(StringComparer.OrdinalIgnoreCase)
        { "CONTACT", "CONTACTSETTING", "MESSAGE", "MESSAGESETTING", "CALLLOG", "CALLOGSETTING", "APPLICATION", "APPSETTING", "OtgBackupTemp" };
    private static readonly HashSet<string> CallRecordingFolders = new(StringComparer.OrdinalIgnoreCase)
        { "TPhoneCallRecords", "CallRecord", "CallRecords", "CallRecording", "CallRecordings", "callar" };

    public static IReadOnlyList<SmartSwitchSourceFile> Enumerate(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot)) return Array.Empty<SmartSwitchSourceFile>();
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var result = new List<SmartSwitchSourceFile>();
        foreach (var path in SafeEnumerateFiles(root))
        {
            try
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || ShouldIgnore(relative)) continue;
                var info = new FileInfo(path);
                result.Add(new SmartSwitchSourceFile(info.FullName, relative, info.Name,
                    Classify(relative, info.Name), info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public static bool TryClassify(string sourceRoot, string path, out string relativePath, out string category)
    {
        relativePath = string.Empty;
        category = string.Empty;
        if (!File.Exists(path)) return false;
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var raw = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        if (ShouldIgnore(raw)) return false;
        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var recording = IsCallRecording(segments, Path.GetFileName(path), Path.GetExtension(path));
        relativePath = NormalizeMobileRelativePath(segments, recording);
        category = recording ? "recording" : Classify(raw, Path.GetFileName(path));
        return true;
    }

    public static string FindModel(string sourceRoot)
    {
        var fromPath = ExtractModel(sourceRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries));
        if (fromPath is not null) return fromPath;
        foreach (var directory in SafeEnumerateDirectories(sourceRoot))
        {
            var model = ExtractModel(new[] { Path.GetFileName(directory) });
            if (model is not null) return model;
        }
        return "모델 미확인";
    }

    public static string? ExtractModel(IEnumerable<string> segments)
    {
        foreach (var segment in segments)
        {
            var match = Regex.Match(segment, @"(?<![A-Z0-9])SM-[A-Z0-9]+", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value.ToUpperInvariant();
        }
        return null;
    }

    private static string Classify(string relativePath, string fileName)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var extension = Path.GetExtension(fileName);
        if (IsCallRecording(segments, fileName, extension)) return "recording";
        if (ImageExtensions.Contains(extension)) return "image";
        if (VideoExtensions.Contains(extension)) return "video";
        if (AudioExtensions.Contains(extension)) return "audio";
        if (DocumentExtensions.Contains(extension)) return "document";
        if (ArchiveExtensions.Contains(extension)) return "archive";
        if (SamsungExtensions.Contains(extension) || segments.Any(SamsungFolders.Contains)) return "samsung";
        if (extension.Equals(".apk", StringComparison.OrdinalIgnoreCase)) return "app";
        return "other";
    }

    private static bool IsCallRecording(IReadOnlyCollection<string> segments, string fileName, string extension)
    {
        if (!AudioExtensions.Contains(extension)) return false;
        if (segments.Any(CallRecordingFolders.Contains)) return true;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Regex.IsMatch(stem, @"_(?:\+?82|0)[0-9\- ]{8,16}_20\d{12}$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(stem, @"^(?:\+?82|0)[0-9\- ]{8,16}_20\d{12}$", RegexOptions.IgnoreCase);
    }

    private static string NormalizeMobileRelativePath(string[] segments, bool isRecording)
    {
        if (!isRecording) return string.Join('/', segments);
        var callIndex = Array.FindIndex(segments, CallRecordingFolders.Contains);
        if (callIndex < 0) return string.Join('/', segments);
        var recordingsIndex = callIndex > 0
            ? Array.FindLastIndex(segments, callIndex - 1, segment => segment.Equals("Recordings", StringComparison.OrdinalIgnoreCase)) : -1;
        if (recordingsIndex >= 0) return string.Join('/', segments[recordingsIndex..]);
        var tPhoneIndex = callIndex > 0
            ? Array.FindLastIndex(segments, callIndex - 1, segment => segment.Equals("TPhone", StringComparison.OrdinalIgnoreCase)) : -1;
        if (tPhoneIndex >= 0) return string.Join('/', segments[tPhoneIndex..]);
        if (segments[callIndex].Equals("TPhoneCallRecords", StringComparison.OrdinalIgnoreCase))
            return string.Join('/', new[] { "Music" }.Concat(segments[callIndex..]));
        return string.Join('/', segments[callIndex..]);
    }

    private static bool ShouldIgnore(string relativePath)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.StartsWith('.') || part.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) ||
               parts.LastOrDefault()?.StartsWith(".thumbdata", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files; string[] directories;
            try { files = Directory.GetFiles(current); directories = Directory.GetDirectories(current); }
            catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var directory in directories) pending.Push(directory);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop(); string[] directories;
            try { directories = Directory.GetDirectories(current); }
            catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var directory in directories) { yield return directory; pending.Push(directory); }
        }
    }
}
