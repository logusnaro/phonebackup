using System.IO;
using Microsoft.Win32;

namespace PhoneBackup.Desktop.Services;

public sealed record SmartSwitchBackupCandidate(
    string RootPath,
    string Model,
    int TotalFiles,
    int RecordingFiles,
    int GeneralFiles,
    DateTimeOffset LastModifiedAt,
    bool HasSamsungManifest)
{
    public string Summary => $"{Model} · 통화녹음 {RecordingFiles}개 · 일반파일 {GeneralFiles}개 · {LastModifiedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
}

public sealed record SmartSwitchDiscoveryProgress(string Location, int LocationsScanned, int CandidatesFound);

/// <summary>
/// Finds Smart Switch backups without assuming one fixed Documents path. The
/// automatic scan uses Samsung settings, prior PB imports, Documents, Desktop
/// and OneDrive. An explicitly selected folder is always scanned recursively
/// and split into one candidate per SM-* model directory.
/// </summary>
public sealed class SmartSwitchDiscoveryService
{
    private static readonly HashSet<string> SearchFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SmartSwitch", "Smart Switch", "SmartSwitchPC"
    };

    private static readonly HashSet<string> SkipFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "node_modules", "packages", "Windows", "Program Files", "Program Files (x86)",
        "$Recycle.Bin", "System Volume Information", "AppData", "PhoneBackup", "PhoneBackupData"
    };

    private readonly DatabaseService _database;

    public SmartSwitchDiscoveryService(DatabaseService database) => _database = database;

    public async Task<IReadOnlyList<SmartSwitchBackupCandidate>> DiscoverDefaultAsync(
        IProgress<SmartSwitchDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var prior = await _database.QueryAsync(
            "SELECT DISTINCT source_path FROM smart_switch_backups ORDER BY started_at DESC",
            r => r.GetString(0));
        var managed = await _database.QueryAsync(
            "SELECT root_path FROM managed_sources ORDER BY last_scan_at DESC",
            r => r.GetString(0));
        var seeds = BuildDefaultSeeds().Concat(prior).Concat(managed).Concat(ReadSamsungConfiguredPaths())
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await Task.Run(() => DiscoverFromSeeds(seeds, false, progress, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<SmartSwitchBackupCandidate>> DiscoverSelectedAsync(
        string selectedRoot,
        IProgress<SmartSwitchDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(selectedRoot))
            throw new DirectoryNotFoundException($"선택한 폴더를 찾을 수 없습니다: {selectedRoot}");
        return Task.Run<IReadOnlyList<SmartSwitchBackupCandidate>>(
            () => DiscoverFromSeeds(new[] { Path.GetFullPath(selectedRoot) }, true, progress, cancellationToken),
            cancellationToken);
    }

    public SmartSwitchBackupCandidate? AnalyzeCandidate(string rootPath)
    {
        var files = SmartSwitchFileClassifier.Enumerate(rootPath);
        if (files.Count == 0) return null;
        var latest = files.Max(file => file.LastModifiedAt);
        var hasManifest = ContainsManifest(rootPath);
        var recordings = files.Count(file => file.Category == "recording");
        return new SmartSwitchBackupCandidate(rootPath, SmartSwitchFileClassifier.FindModel(rootPath),
            files.Count, recordings, files.Count - recordings, latest, hasManifest);
    }

    private IReadOnlyList<SmartSwitchBackupCandidate> DiscoverFromSeeds(
        IEnumerable<string> seeds,
        bool selectedByUser,
        IProgress<SmartSwitchDiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            progress?.Report(new SmartSwitchDiscoveryProgress(seed, scanned, roots.Count));
            foreach (var candidateRoot in FindCandidateRoots(seed, selectedByUser, cancellationToken))
                roots.Add(candidateRoot);
        }

        var candidates = new List<SmartSwitchBackupCandidate>();
        foreach (var root in RemoveNestedDuplicates(roots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = AnalyzeCandidate(root);
            if (candidate is not null) candidates.Add(candidate);
            progress?.Report(new SmartSwitchDiscoveryProgress(root, scanned, candidates.Count));
        }
        return candidates
            .OrderByDescending(candidate => candidate.LastModifiedAt)
            .ThenBy(candidate => candidate.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> FindCandidateRoots(string seed, bool selectedByUser, CancellationToken cancellationToken)
    {
        var fullSeed = Path.GetFullPath(seed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var seedModel = SmartSwitchFileClassifier.ExtractModel(fullSeed.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries));
        if (seedModel is not null)
        {
            yield return fullSeed;
            yield break;
        }

        if (selectedByUser || SearchFolderNames.Contains(Path.GetFileName(fullSeed)) || ContainsManifest(fullSeed))
        {
            var modelDirectories = FindModelDirectories(fullSeed, cancellationToken).ToList();
            if (modelDirectories.Count == 0) yield return fullSeed;
            else foreach (var modelDirectory in modelDirectories) yield return modelDirectory;
            yield break;
        }

        // During automatic discovery, do not treat every unrelated folder named
        // SM-* as a Samsung backup. First anchor the scan to an actual
        // SmartSwitch directory, then split only that tree by model.
        var namedRoots = FindNamedSmartSwitchRoots(fullSeed, 5, cancellationToken).ToList();
        foreach (var namedRoot in namedRoots.Where(candidate => !namedRoots.Any(parent =>
                     !parent.Equals(candidate, StringComparison.OrdinalIgnoreCase) && IsWithin(candidate, parent))))
        {
            var nestedModels = FindModelDirectories(namedRoot, cancellationToken).ToList();
            if (nestedModels.Count == 0) yield return namedRoot;
            else foreach (var modelDirectory in nestedModels) yield return modelDirectory;
        }
    }

    private static IEnumerable<string> FindModelDirectories(string root, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var directory in SafeBreadthFirstDirectories(root, 10, cancellationToken))
        {
            if (SmartSwitchFileClassifier.ExtractModel(new[] { Path.GetFileName(directory) }) is null) continue;
            if (result.Any(existing => IsWithin(directory, existing))) continue;
            result.Add(directory);
        }
        return result;
    }

    private static IEnumerable<string> FindNamedSmartSwitchRoots(string root, int maxDepth, CancellationToken cancellationToken)
        => SafeBreadthFirstDirectories(root, maxDepth, cancellationToken)
            .Where(directory => SearchFolderNames.Contains(Path.GetFileName(directory)));

    private static IEnumerable<string> SafeBreadthFirstDirectories(string root, int maxDepth, CancellationToken cancellationToken)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < 30_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();
            if (depth >= maxDepth) continue;
            string[] directories;
            try { directories = Directory.GetDirectories(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
            {
                visited++;
                var name = Path.GetFileName(directory);
                if (SkipFolderNames.Contains(name) || name.StartsWith('.')) continue;
                yield return directory;
                pending.Enqueue((directory, depth + 1));
            }
        }
    }

    private static IEnumerable<string> RemoveNestedDuplicates(IEnumerable<string> roots)
    {
        var ordered = roots.Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();
        foreach (var root in ordered)
        {
            if (ordered.Any(other => !other.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                     SmartSwitchFileClassifier.FindModel(other).Equals(SmartSwitchFileClassifier.FindModel(root), StringComparison.OrdinalIgnoreCase) &&
                                     IsWithin(root, other))) continue;
            yield return root;
        }
    }

    private static bool IsWithin(string candidate, string parent)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsManifest(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "ReqItemsInfo.json", SearchOption.AllDirectories).Any() ||
                   Directory.EnumerateFiles(root, "SmartSwitchBackup*.json", SearchOption.AllDirectories).Any() ||
                   Directory.EnumerateFiles(root, "*.spbm", SearchOption.AllDirectories).Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> BuildDefaultSeeds()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var roots = new List<string?>
        {
            Path.Combine(documents, "Samsung", "SmartSwitch", "backup"),
            Path.Combine(documents, "Samsung", "SmartSwitch"),
            Path.Combine(profile, "Documents", "Samsung", "SmartSwitch", "backup"),
            Path.Combine(profile, "Downloads"),
            documents,
            desktop,
            commonDocuments,
            profile
        };
        roots.AddRange(Environment.GetEnvironmentVariables().Keys.Cast<object>()
            .Select(key => key.ToString())
            .Where(key => key is not null && key.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            .Select(key => Environment.GetEnvironmentVariable(key!)));
        return roots.Where(path => !string.IsNullOrWhiteSpace(path))!.Cast<string>();
    }

    private static IEnumerable<string> ReadSamsungConfiguredPaths()
    {
        var keyNames = new[]
        {
            @"Software\Samsung\SmartSwitchPC",
            @"Software\Samsung\Smart Switch PC",
            @"Software\Samsung\SmartSwitch"
        };
        foreach (var keyName in keyNames)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyName);
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string value || !Directory.Exists(value)) continue;
                yield return value;
            }
        }
    }
}
