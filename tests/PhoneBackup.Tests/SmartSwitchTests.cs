using PhoneBackup.Desktop.Services;
using Xunit;

namespace PhoneBackup.Tests;

public sealed class SmartSwitchTests
{
    [Fact]
    public void Classifier_NormalizesCallRecordingToPhonePath()
    {
        using var folder = new TemporaryFolder();
        var source = folder.CreateDirectory("backup/SM-A175N/20260901");
        var file = folder.CreateFile("backup/SM-A175N/20260901/MUSIC/TPhoneCallRecords/홍길동.인사팀장.서울병원_01012345678_20240101112233.m4a");
        Assert.True(SmartSwitchFileClassifier.TryClassify(source, file, out var relative, out var category));
        Assert.Equal("recording", category);
        Assert.Equal("Music/TPhoneCallRecords/홍길동.인사팀장.서울병원_01012345678_20240101112233.m4a", relative);
    }

    [Fact]
    public void Classifier_IndexesUnknownAndSamsungContainersInsteadOfHidingThem()
    {
        using var folder = new TemporaryFolder();
        var source = folder.CreateDirectory("backup/SM-A175N/session");
        folder.CreateFile("backup/SM-A175N/session/CONTACT/contacts.spbm");
        folder.CreateFile("backup/SM-A175N/session/MYSTERY/payload.unknown");
        var files = SmartSwitchFileClassifier.Enumerate(source);
        Assert.Contains(files, file => file.Category == "samsung" && file.OriginalFileName == "contacts.spbm");
        Assert.Contains(files, file => file.Category == "other" && file.OriginalFileName == "payload.unknown");
    }

    [Fact]
    public async Task Discovery_SplitsOneParentIntoModelCandidates()
    {
        using var folder = new TemporaryFolder();
        using var database = new DatabaseService(Path.Combine(folder.Root, "pb.db"));
        await database.InitializeAsync();
        folder.CreateFile("SmartSwitch/backup/SM-A175N/MUSIC/TPhoneCallRecords/01011112222_20240101101010.m4a");
        folder.CreateFile("SmartSwitch/backup/SM-G930S/DCIM/Camera/image.jpg");
        var candidates = await new SmartSwitchDiscoveryService(database)
            .DiscoverSelectedAsync(Path.Combine(folder.Root, "SmartSwitch"));
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Model == "SM-A175N" && candidate.RecordingFiles == 1);
        Assert.Contains(candidates, candidate => candidate.Model == "SM-G930S" && candidate.GeneralFiles == 1);
    }

    [Fact]
    public async Task Catalog_IndexesInPlaceAndVerificationHashesWithoutCopying()
    {
        using var folder = new TemporaryFolder();
        using var database = new DatabaseService(Path.Combine(folder.Root, "data", "pb.db"));
        await database.InitializeAsync();
        var source = folder.CreateDirectory("source/SM-A175N/session");
        var recording = folder.CreateFile("source/SM-A175N/session/MUSIC/TPhoneCallRecords/01011112222_20240101101010.m4a", "recording-content");
        folder.CreateFile("source/SM-A175N/session/DOCUMENT/report.pdf", "document-content");
        var catalog = new SmartSwitchCatalogService(database);

        var indexed = await catalog.IndexAsync(source);
        var verified = await catalog.VerifyAsync(indexed.SourceId);

        Assert.Equal(2, indexed.TotalFiles);
        Assert.Equal(2, indexed.Added);
        Assert.Equal(2, verified.Verified);
        var rows = await database.QueryAsync(
            "SELECT full_path,category,sha256 FROM managed_files ORDER BY category",
            reader => (Path: reader.GetString(0), Category: reader.GetString(1), Sha: reader.GetString(2)));
        Assert.Contains(rows, row => row.Path == recording && row.Category == "recording" && row.Sha.Length == 64);
    }

    [Fact]
    public async Task Catalog_MarksRemovedSourceFileMissingWithoutDeletingAnythingElse()
    {
        using var folder = new TemporaryFolder();
        using var database = new DatabaseService(Path.Combine(folder.Root, "data", "pb.db"));
        await database.InitializeAsync();
        var source = folder.CreateDirectory("source/SM-G930S/session");
        var path = folder.CreateFile("source/SM-G930S/session/DCIM/Camera/image.jpg");
        var catalog = new SmartSwitchCatalogService(database);
        await catalog.IndexAsync(source);
        File.Delete(path);

        var rescanned = await catalog.IndexAsync(source);

        Assert.Equal(1, rescanned.Missing);
        var states = await database.QueryAsync("SELECT state FROM managed_files", reader => reader.GetInt32(0));
        Assert.Equal(2, Assert.Single(states));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"PhoneBackupTests-{Guid.NewGuid():N}");
        public TemporaryFolder() => Directory.CreateDirectory(Root);
        public string CreateDirectory(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path); return path;
        }
        public string CreateFile(string relative, string content = "test-content")
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
