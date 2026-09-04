using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PhoneBackup.Desktop.Services;

namespace PhoneBackup.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SourceRow> _sources = new();
    private readonly ObservableCollection<CatalogFileRow> _recordings = new();
    private readonly ObservableCollection<CatalogFileRow> _images = new();
    private readonly ObservableCollection<CatalogFileRow> _videos = new();
    private readonly ObservableCollection<CatalogFileRow> _documents = new();
    private readonly ObservableCollection<CatalogFileRow> _audio = new();
    private readonly ObservableCollection<CatalogFileRow> _other = new();
    private CancellationTokenSource? _workCancellation;
    private DataGrid? _lastFileGrid;

    public MainWindow()
    {
        InitializeComponent();
        SourcesGrid.ItemsSource = _sources;
        HomeSourcesGrid.ItemsSource = _sources;
        RecordingGrid.ItemsSource = _recordings;
        ImageGrid.ItemsSource = _images;
        VideoGrid.ItemsSource = _videos;
        DocumentGrid.ItemsSource = _documents;
        AudioGrid.ItemsSource = _audio;
        OtherGrid.ItemsSource = _other;
        Loaded += async (_, _) =>
        {
            await RefreshAllAsync();
            ApplyRecordingGrouping();
        };
    }

    private void OpenSmartSwitch_Click(object sender, RoutedEventArgs e)
    {
        var opened = App.Services.SmartSwitch.TryOpenApplication(out var message);
        StatusText.Text = message;
        if (!opened) MessageBox.Show(this, message, "Smart Switch", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void AutoDiscover_Click(object sender, RoutedEventArgs e)
    {
        await RunWorkAsync("Smart Switch 백업을 찾는 중", async (progress, token) =>
        {
            var discoveryProgress = new Progress<SmartSwitchDiscoveryProgress>(item =>
            {
                ProgressDetail.Text = $"검색 위치 {item.LocationsScanned}개 · 후보 {item.CandidatesFound}개 · {item.Location}";
                WorkProgress.IsIndeterminate = true;
            });
            var candidates = await App.Services.SmartSwitchDiscovery.DiscoverDefaultAsync(discoveryProgress, token);
            if (candidates.Count == 0)
            {
                MessageBox.Show(this, "Smart Switch 백업 폴더를 자동으로 찾지 못했습니다.\n‘폴더 직접 추가’로 백업 폴더 또는 그 상위 폴더를 선택해 주세요.",
                    "백업을 찾지 못함", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                ProgressTitle.Text = $"{candidate.Model} 백업을 정리하는 중";
                await App.Services.Catalog.IndexAsync(candidate.RootPath, progress, token);
            }
            StatusText.Text = $"백업 폴더 {candidates.Count}개를 찾아 최신 상태로 정리했습니다.";
        });
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Smart Switch 백업 폴더 또는 상위 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await RunWorkAsync("선택한 백업을 확인하는 중", async (progress, token) =>
        {
            var candidates = await App.Services.SmartSwitchDiscovery.DiscoverSelectedAsync(dialog.FolderName, cancellationToken: token);
            if (candidates.Count == 0)
            {
                await App.Services.Catalog.IndexAsync(dialog.FolderName, progress, token);
                StatusText.Text = "선택한 폴더를 등록했습니다.";
                return;
            }
            foreach (var candidate in candidates) await App.Services.Catalog.IndexAsync(candidate.RootPath, progress, token);
            StatusText.Text = $"{candidates.Count}개 백업 폴더를 등록했습니다.";
        });
    }

    private async void RescanSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesGrid.SelectedItem is not SourceRow source) { SelectSourceMessage(); return; }
        if (!Directory.Exists(source.RootPath))
        {
            MessageBox.Show(this, "원본 폴더가 이동되었거나 연결되지 않았습니다.", "폴더 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunWorkAsync($"{source.Model} 다시 색인", async (progress, token) =>
        {
            var result = await App.Services.Catalog.IndexAsync(source.RootPath, progress, token);
            StatusText.Text = $"색인 완료 · 신규 {result.Added:N0} · 변경 {result.Updated:N0} · 누락 {result.Missing:N0}";
        });
    }

    private async void VerifySource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesGrid.SelectedItem is not SourceRow source) { SelectSourceMessage(); return; }
        await RunWorkAsync($"{source.Model} 무결성 확인", async (progress, token) =>
        {
            var result = await App.Services.Catalog.VerifyAsync(source.Id, progress, token);
            StatusText.Text = $"무결성 확인 완료 · 정상 {result.Verified:N0} · 변경 {result.Changed:N0} · 없음 {result.Missing:N0}";
            if (result.Changed + result.Missing > 0)
                MessageBox.Show(this, "변경되거나 찾을 수 없는 파일이 있습니다. Smart Switch 백업 폴더 상태를 확인해 주세요.",
                    "확인 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesGrid.SelectedItem is not SourceRow source) { SelectSourceMessage(); return; }
        OpenPath(source.RootPath);
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesGrid.SelectedItem is not SourceRow source) { SelectSourceMessage(); return; }
        if (MessageBox.Show(this,
                $"PB 목록에서 ‘{source.DisplayName}’을 제거할까요?\n\nSmart Switch 원본 파일은 삭제되지 않습니다.",
                "백업 폴더 목록 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await App.Services.Catalog.RemoveSourceAsync(source.Id);
        await RefreshAllAsync();
        StatusText.Text = "PB 색인만 제거했습니다. Smart Switch 원본은 그대로입니다.";
    }

    private async Task RunWorkAsync(string title,
        Func<IProgress<CatalogProgress>, CancellationToken, Task> action)
    {
        if (_workCancellation is not null) return;
        _workCancellation = new CancellationTokenSource();
        ProgressTitle.Text = title;
        ProgressDetail.Text = "준비 중…";
        WorkProgress.IsIndeterminate = true;
        ProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<CatalogProgress>(item =>
        {
            WorkProgress.IsIndeterminate = item.Total <= 0;
            if (item.Total > 0) WorkProgress.Value = item.Processed * 100d / item.Total;
            ProgressDetail.Text = $"{item.Stage} · {item.Processed:N0}/{item.Total:N0} · {item.CurrentFile}";
        });
        try
        {
            await action(progress, _workCancellation.Token);
            await RefreshAllAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "작업을 취소했습니다."; }
        catch (Exception ex)
        {
            App.LogCrash("CatalogWork", ex);
            StatusText.Text = $"작업 실패: {ex.Message}";
            MessageBox.Show(this, $"작업을 완료하지 못했습니다.\n\n{ex.Message}", "PB 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _workCancellation.Dispose();
            _workCancellation = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            WorkProgress.Value = 0;
        }
    }

    private void CancelWork_Click(object sender, RoutedEventArgs e) => _workCancellation?.Cancel();

    private async Task RefreshAllAsync()
    {
        await RefreshSourcesAsync();
        await LoadFilesAsync("recording", RecordingSearch?.Text ?? string.Empty, _recordings);
        await LoadFilesAsync("image", ImageSearch?.Text ?? string.Empty, _images);
        await LoadFilesAsync("video", VideoSearch?.Text ?? string.Empty, _videos);
        await LoadFilesAsync("document", DocumentSearch?.Text ?? string.Empty, _documents);
        await LoadFilesAsync("audio", AudioSearch?.Text ?? string.Empty, _audio);
        await LoadFilesAsync("other", OtherSearch?.Text ?? string.Empty, _other);
        ApplyRecordingGrouping();
        SourceCountText.Text = _sources.Count.ToString("N0");
        FileCountText.Text = (_recordings.Count + _images.Count + _videos.Count + _documents.Count + _audio.Count + _other.Count).ToString("N0");
        RecordingCountText.Text = _recordings.Count.ToString("N0");
        TotalSizeText.Text = HumanSize(_sources.Sum(source => source.TotalBytes));
        VerifiedCountText.Text = (await App.Services.Database.QueryAsync(
            "SELECT COUNT(*) FROM managed_files WHERE verified_at IS NOT NULL AND state=0", reader => reader.GetInt32(0))).FirstOrDefault().ToString("N0");
    }

    private async Task RefreshSourcesAsync()
    {
        var rows = await App.Services.Database.QueryAsync("""
            SELECT id,root_path,display_name,model,last_scan_at,files_count,total_bytes,status
            FROM managed_sources ORDER BY COALESCE(last_scan_at,created_at) DESC
            """, reader => new SourceRow(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.GetInt32(5), reader.GetInt64(6), reader.GetInt32(7)));
        _sources.Clear();
        foreach (var row in rows) _sources.Add(row);
    }

    private async Task LoadFilesAsync(string category, string term, ObservableCollection<CatalogFileRow> target)
    {
        var filter = category == "other"
            ? "f.category NOT IN ('recording','image','video','document','audio')"
            : "f.category=$category";
        var rows = await App.Services.Database.QueryAsync($"""
            SELECT f.id,f.source_id,s.display_name,s.model,f.full_path,f.relative_path,f.original_file_name,
              f.category,f.size_bytes,f.last_modified_at,f.recorded_at,f.parsed_phone_number,
              f.parsed_contact_name,f.parsed_target,f.parsed_affiliation,f.sha256,f.verified_at,f.state
            FROM managed_files f JOIN managed_sources s ON s.id=f.source_id
            WHERE {filter} AND ($term='' OR f.original_file_name LIKE $like OR f.relative_path LIKE $like
              OR COALESCE(f.parsed_phone_number,'') LIKE $like OR COALESCE(f.parsed_contact_name,'') LIKE $like
              OR COALESCE(f.parsed_affiliation,'') LIKE $like)
            ORDER BY COALESCE(f.recorded_at,f.last_modified_at) DESC
            """, reader => new CatalogFileRow(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
                DateTimeOffset.Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)), reader.GetInt32(17)),
            parameters =>
            {
                if (category != "other") parameters.AddWithValue("$category", category);
                parameters.AddWithValue("$term", term.Trim());
                parameters.AddWithValue("$like", $"%{term.Trim()}%");
            });
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private async void SearchRecording_Click(object sender, RoutedEventArgs e)
    {
        await LoadFilesAsync("recording", RecordingSearch.Text, _recordings);
        ApplyRecordingGrouping();
    }

    private async void SearchCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        switch (category)
        {
            case "image": await LoadFilesAsync(category, ImageSearch.Text, _images); break;
            case "video": await LoadFilesAsync(category, VideoSearch.Text, _videos); break;
            case "document": await LoadFilesAsync(category, DocumentSearch.Text, _documents); break;
            case "audio": await LoadFilesAsync(category, AudioSearch.Text, _audio); break;
            default: await LoadFilesAsync("other", OtherSearch.Text, _other); break;
        }
    }

    private void RecordingGroup_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyRecordingGrouping();

    private void ApplyRecordingGrouping()
    {
        if (RecordingGrid is null) return;
        var view = CollectionViewSource.GetDefaultView(_recordings);
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            var choice = (RecordingGroup?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (choice == "날짜별") view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogFileRow.DateGroup)));
            else if (choice == "상대방별") view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogFileRow.CallerGroup)));
        }
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid) _lastFileGrid = grid;
    }

    private void ImageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _lastFileGrid = ImageGrid;
        ImagePreview.Source = null;
        ImagePreviewHint.Visibility = Visibility.Visible;
        if (ImageGrid.SelectedItem is not CatalogFileRow row || !File.Exists(row.FullPath)) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 700;
            bitmap.UriSource = new Uri(row.FullPath);
            bitmap.EndInit();
            bitmap.Freeze();
            ImagePreview.Source = bitmap;
            ImagePreviewHint.Visibility = Visibility.Collapsed;
        }
        catch { ImagePreviewHint.Text = "이 형식은 미리보기를 지원하지 않습니다.\n‘파일 열기’를 이용하세요."; }
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile() is { } row) OpenPath(row.FullPath);
        else SelectFileMessage();
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedFile();
        if (row is null) { SelectFileMessage(); return; }
        if (!File.Exists(row.FullPath)) { MessageBox.Show(this, "파일을 찾을 수 없습니다. 폴더를 다시 색인해 주세요.", "파일 없음"); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FullPath}\"") { UseShellExecute = true });
    }

    private void FileGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: CatalogFileRow row }) OpenPath(row.FullPath);
    }

    private CatalogFileRow? SelectedFile()
    {
        var grid = MainTabs.SelectedIndex switch
        {
            1 => RecordingGrid,
            2 => ImageGrid,
            3 => VideoGrid,
            4 => DocumentGrid,
            5 => AudioGrid,
            6 => OtherGrid,
            _ => _lastFileGrid
        };
        return grid?.SelectedItem as CatalogFileRow;
    }

    private static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ZIP 파일|*.zip", FileName = $"PB-error-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        await App.Services.Diagnostics.ExportAsync(dialog.FileName);
        StatusText.Text = "개인정보를 제거한 오류 리포트를 저장했습니다.";
    }

    private void SelectSourceMessage() => MessageBox.Show(this, "백업 폴더 목록에서 항목을 먼저 선택하세요.", "선택 필요");
    private void SelectFileMessage() => MessageBox.Show(this, "파일을 먼저 선택하세요.", "선택 필요");

    private static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var size = (double)Math.Max(0, bytes); var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    public sealed record SourceRow(Guid Id, string RootPath, string DisplayName, string Model,
        DateTimeOffset? LastScanAt, int FilesCount, long TotalBytes, int Status)
    {
        public string FileCountText => FilesCount.ToString("N0");
        public string SizeText => HumanSize(TotalBytes);
        public string LastScanText => LastScanAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "아직 안 함";
        public string StatusText => !Directory.Exists(RootPath) ? "폴더 없음" : Status == 0 ? "정상" : "확인 필요";
    }

    public sealed record CatalogFileRow(Guid Id, Guid SourceId, string SourceName, string Model,
        string FullPath, string RelativePath, string FileName, string Category, long SizeBytes,
        DateTimeOffset ModifiedAt, DateTimeOffset? RecordedAt, string? Phone, string? Contact,
        string? Target, string? Affiliation, string? Sha256, DateTimeOffset? VerifiedAt, int State)
    {
        public string SizeText => HumanSize(SizeBytes);
        public string ModifiedText => ModifiedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        public string RecordedText => (RecordedAt ?? ModifiedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        public string DateGroup => (RecordedAt ?? ModifiedAt).LocalDateTime.ToString("yyyy년 MM월 dd일");
        public string CallerText => Contact ?? Phone ?? "미확인";
        public string CallerGroup => CallerText;
        public string TargetText => Target ?? "미확인";
        public string AffiliationText => Affiliation ?? "-";
        public string PhoneText => Phone ?? "-";
        public string RelativeFolder => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/') ?? "-";
        public string StateText => State switch { 1 => "변경됨", 2 => "파일 없음", _ when VerifiedAt is not null => "확인 완료", _ => "색인 완료" };
        public string CategoryText => Category switch { "archive" => "압축 파일", "samsung" => "삼성 백업 데이터", "app" => "앱", _ => "기타" };
    }
}
