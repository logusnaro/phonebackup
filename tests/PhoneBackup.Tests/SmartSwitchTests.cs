using PhoneBackup.Desktop.Models;
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

        var classified = SmartSwitchFileClassifier.TryClassify(source, file, out var relative, out var category);

        Assert.True(classified);
        Assert.Equal("recording", category);
        Assert.Equal("Music/TPhoneCallRecords/홍길동.인사팀장.서울병원_01012345678_20240101112233.m4a", relative);
    }

    [Fact]
    public void Classifier_UsesFilenamePatternWhenFolderNameVaries()
    {
        using var folder = new TemporaryFolder();
        var source = folder.CreateDirectory("custom");
        var file = folder.CreateFile("custom/unknown/01027315428_20240125180703.m4a");

        Assert.True(SmartSwitchFileClassifier.TryClassify(source, file, out _, out var category));
        Assert.Equal("recording", category);
    }

    [Fact]
    public void Classifier_NormalizesModernRecordingsPathWithoutSmartSwitchWrapper()
    {
        using var folder = new TemporaryFolder();
        var source = folder.CreateDirectory("backup/SM-A175N");
        var file = folder.CreateFile("backup/SM-A175N/session/Music/Recordings/TPhoneCallRecords/01027315428_20240125180703.m4a");

        Assert.True(SmartSwitchFileClassifier.TryClassify(source, file, out var relative, out var category));
        Assert.Equal("recording", category);
        Assert.Equal("Recordings/TPhoneCallRecords/01027315428_20240125180703.m4a", relative);
    }

    [Fact]
    public void Classifier_IgnoresPrivateContactContainers()
    {
        using var folder = new TemporaryFolder();
        var source = folder.CreateDirectory("backup");
        var file = folder.CreateFile("backup/CONTACT/export.txt");

        Assert.False(SmartSwitchFileClassifier.TryClassify(source, file, out _, out _));
    }

    [Fact]
    public void Classifier_IgnoresSamsungSettingsArchivesAndAppIcons()
    {
        using var folder = new TemporaryFolder();
        var source = folder.CreateDirectory("backup/SM-A175N/session");
        var settings = folder.CreateFile("backup/SM-A175N/session/SMARTTHINGS/settings.zip");
        var appIcon = folder.CreateFile("backup/SM-A175N/session/APKFILE/icon.png");

        Assert.False(SmartSwitchFileClassifier.TryClassify(source, settings, out _, out _));
        Assert.False(SmartSwitchFileClassifier.TryClassify(source, appIcon, out _, out _));
    }

    [Fact]
    public async Task Discovery_SplitsOneParentIntoModelCandidates()
    {
        using var folder = new TemporaryFolder();
        var database = new DatabaseService(Path.Combine(folder.Root, "pb.db"));
        await database.InitializeAsync();
        folder.CreateFile("SmartSwitch/backup/SM-A175N/MUSIC/TPhoneCallRecords/01011112222_20240101101010.m4a");
        folder.CreateFile("SmartSwitch/backup/SM-G930S/DCIM/Camera/image.jpg");
        var discovery = new SmartSwitchDiscoveryService(database);

        var candidates = await discovery.DiscoverSelectedAsync(Path.Combine(folder.Root, "SmartSwitch"));

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Model == "SM-A175N" && candidate.RecordingFiles == 1);
        Assert.Contains(candidates, candidate => candidate.Model == "SM-G930S" && candidate.GeneralFiles == 1);
    }

    [Fact]
    public async Task ImportAndVerify_LinkMatchingPhoneAndAllowMissingOptionalManifests()
    {
        using var folder = new TemporaryFolder();
        using var database = new DatabaseService(Path.Combine(folder.Root, "data", "pb.db"));
        await database.InitializeAsync();
        var memberId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        await database.ExecuteAsync("""
            INSERT INTO members(id,name,status,created_at) VALUES($member,'팀원',0,$at);
            INSERT INTO devices(id,member_id,display_name,platform,model,status,last_seen_at,token_hash)
            VALUES($device,$member,'팀원폰','Android','SM-A175N',1,$at,'token')
            """, parameters =>
        {
            parameters.AddWithValue("$member", memberId.ToString());
            parameters.AddWithValue("$device", deviceId.ToString());
            parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        });
        var source = folder.CreateDirectory("source/SM-A175N/session");
        folder.CreateFile("source/SM-A175N/session/MUSIC/TPhoneCallRecords/01011112222_20240101101010.m4a", "recording-content");
        folder.CreateFile("source/SM-A175N/session/DOCUMENT/report.pdf", "document-content");
        var backups = new BackupService(database) { Root = folder.CreateDirectory("pb-files") };
        var importer = new SmartSwitchImportService(database, backups);
        var verifier = new SmartSwitchService(database);

        var imported = await importer.ImportAsync(memberId, source);
        var verification = await verifier.VerifyAsync(imported.DeviceId, source);

        Assert.Equal(deviceId, imported.DeviceId);
        Assert.True(imported.LinkedToPairedPhone);
        Assert.Equal(2, imported.Stored);
        Assert.True(verification.Success);
        Assert.Equal(2, verification.VerifiedFiles);
        Assert.Contains(verification.Warnings, warning => warning.Contains("ReqItemsInfo.json", StringComparison.Ordinal));
        var items = await database.QueryAsync(
            "SELECT relative_path,category FROM backup_items ORDER BY category",
            reader => (Path: reader.GetString(0), Category: reader.GetString(1)));
        Assert.Contains(items, item => item.Path.StartsWith("Music/TPhoneCallRecords/", StringComparison.Ordinal) && item.Category == "recording");
    }

    [Fact]
    public async Task Import_DoesNotLinkDifferentPhoneModelForDeletion()
    {
        using var folder = new TemporaryFolder();
        using var database = new DatabaseService(Path.Combine(folder.Root, "data", "pb.db"));
        await database.InitializeAsync();
        var memberId = Guid.NewGuid();
        await database.ExecuteAsync("""
            INSERT INTO members(id,name,status,created_at) VALUES($member,'팀원',0,$at);
            INSERT INTO devices(id,member_id,display_name,platform,model,status,last_seen_at,token_hash)
            VALUES($device,$member,'새폰','Android','SM-A175N',1,$at,'token')
            """, parameters =>
        {
            parameters.AddWithValue("$member", memberId.ToString());
            parameters.AddWithValue("$device", Guid.NewGuid().ToString());
            parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        });
        var source = folder.CreateDirectory("source/SM-G930S/session");
        folder.CreateFile("source/SM-G930S/session/MUSIC/TPhoneCallRecords/01011112222_20240101101010.m4a");
        var backups = new BackupService(database) { Root = folder.CreateDirectory("pb-files") };

        var imported = await new SmartSwitchImportService(database, backups).ImportAsync(memberId, source);

        Assert.False(imported.LinkedToPairedPhone);
        var platform = await database.QueryAsync("SELECT platform FROM devices WHERE id=$id", reader => reader.GetString(0),
            parameters => parameters.AddWithValue("$id", imported.DeviceId.ToString()));
        Assert.Equal("SmartSwitch", Assert.Single(platform));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"PhoneBackupTests-{Guid.NewGuid():N}");

        public TemporaryFolder() => Directory.CreateDirectory(Root);

        public string CreateDirectory(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string relative, string content = "test-content")
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
