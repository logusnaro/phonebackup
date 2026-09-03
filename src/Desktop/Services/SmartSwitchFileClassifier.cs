using System.IO;
using System.Text.RegularExpressions;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchSourceFile(
    string FullPath,
    string RelativePath,
    string OriginalFileName,
    string Category,
    long SizeBytes,
    DateTimeOffset LastModifiedAt);

/// <summary>
/// Converts the many Smart Switch folder layouts into stable, phone-relative
/// paths. It intentionally ignores Samsung's private database containers and
/// only indexes ordinary files that can be verified byte-for-byte.
/// </summary>
public static class SmartSwitchFileClassifier
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".amr", ".3ga", ".3gp", ".wav", ".mp3", ".aac", ".ogg", ".flac",
        ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp", ".bmp",
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".hwp", ".hwpx", ".cell", ".show", ".txt", ".csv", ".rtf", ".zip"
    };

    private static readonly HashSet<string> PrivateFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AccountsIcons", "CATEGORY_ICON", "CONTACT", "CONTACTSETTING", "MESSAGE", "MESSAGESETTING",
        "CALLLOG", "CALLOGSETTING", "OtgBackupTemp", "APPLICATION", "APPSETTING"
    };

    private static readonly HashSet<string> CallRecordingFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "TPhoneCallRecords", "CallRecord", "CallRecords", "CallRecording", "CallRecordings", "callar"
    };

    private static readonly HashSet<string> UserFileContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Music", "PHOTO", "PHOTOS", "VIDEO", "VIDEOS", "DOCUMENT", "DOCUMENTS", "ETCFOLDER",
        "DCIM", "Pictures", "Movies", "Download", "Downloads", "KakaoTalkDownload", "KakaoTalk",
        "TPhone", "Recordings", "Recorder", "callar"
    };

    private static readonly Dictionary<string, string> MobileRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DCIM"] = "DCIM",
        ["Pictures"] = "Pictures",
        ["Movies"] = "Movies",
        ["Music"] = "Music",
        ["Documents"] = "Documents",
        ["Download"] = "Download",
        ["Downloads"] = "Download",
        ["KakaoTalkDownload"] = "KakaoTalkDownload",
        ["KakaoTalk"] = "KakaoTalk",
        ["TPhone"] = "TPhone",
        ["Recordings"] = "Recordings",
        ["Recorder"] = "Recorder",
        ["callar"] = "callar"
    };

    public static IReadOnlyList<SmartSwitchSourceFile> Enumerate(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot)) return Array.Empty<SmartSwitchSourceFile>();
        var result = new List<SmartSwitchSourceFile>();
        foreach (var path in SafeEnumerateFiles(sourceRoot))
        {
            if (!TryClassify(sourceRoot, path, out var relativePath, out var category)) continue;
            try
            {
                var file = new FileInfo(path);
                result.Add(new SmartSwitchSourceFile(file.FullName, relativePath, file.Name, category,
                    file.Length, new DateTimeOffset(file.LastWriteTimeUtc)));
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
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return false;

        var rawRelative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        var segments = rawRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => PrivateFolders.Contains(segment))) return false;

        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension)) return false;

        var fileName = Path.GetFileName(path);
        var isCallRecording = IsCallRecording(segments, fileName, extension);
        if (!isCallRecording && !segments.Any(segment => UserFileContainers.Contains(segment))) return false;
        category = isCallRecording ? "recording" : extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".heic" or ".webp" or ".bmp" => "image",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".wmv" => "video",
            ".m4a" or ".amr" or ".3ga" or ".3gp" or ".wav" or ".mp3" or ".aac" or ".ogg" or ".flac" => "audio",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or
                ".hwp" or ".hwpx" or ".cell" or ".show" or ".txt" or ".csv" or ".rtf" or ".zip" => "document",
            _ => string.Empty
        };
        if (category.Length == 0) return false;

        relativePath = NormalizeMobileRelativePath(segments, isCallRecording);
        return relativePath.Length > 0;
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

    private static bool IsCallRecording(IReadOnlyCollection<string> segments, string fileName, string extension)
    {
        if (!extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".amr", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".3ga", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) return false;
        if (segments.Any(segment => CallRecordingFolders.Contains(segment))) return true;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Regex.IsMatch(stem, @"_(?:\+?82|0)[0-9\- ]{8,16}_20\d{12}$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(stem, @"^(?:\+?82|0)[0-9\- ]{8,16}_20\d{12}$", RegexOptions.IgnoreCase);
    }

    private static string NormalizeMobileRelativePath(string[] segments, bool isCallRecording)
    {
        if (isCallRecording)
        {
            var callIndex = Array.FindIndex(segments, segment => CallRecordingFolders.Contains(segment));
            if (callIndex >= 0)
            {
                var recordingsIndex = callIndex > 0
                    ? Array.FindLastIndex(segments, callIndex - 1,
                        segment => segment.Equals("Recordings", StringComparison.OrdinalIgnoreCase))
                    : -1;
                if (recordingsIndex >= 0) return string.Join('/', segments[recordingsIndex..]);
                var tPhoneIndex = callIndex > 0
                    ? Array.FindLastIndex(segments, callIndex - 1,
                        segment => segment.Equals("TPhone", StringComparison.OrdinalIgnoreCase))
                    : -1;
                if (tPhoneIndex >= 0) return string.Join('/', segments[tPhoneIndex..]);
                if (segments[callIndex].Equals("TPhoneCallRecords", StringComparison.OrdinalIgnoreCase))
                    return string.Join('/', new[] { "Music" }.Concat(segments[callIndex..]));
                return string.Join('/', segments[callIndex..]);
            }
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!MobileRootNames.TryGetValue(segments[index], out var canonical)) continue;
            var normalized = segments[index..].ToArray();
            normalized[0] = canonical;
            return string.Join('/', normalized);
        }

        var modelIndex = Array.FindIndex(segments,
            segment => ExtractModel(new[] { segment }) is not null);
        var start = modelIndex >= 0 && modelIndex + 1 < segments.Length ? modelIndex + 1 : 0;
        return string.Join('/', segments[start..]);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current).ToArray();
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var directory in directories) pending.Push(directory);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] directories;
            try { directories = Directory.GetDirectories(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
            {
                yield return directory;
                pending.Push(directory);
            }
        }
    }
}
