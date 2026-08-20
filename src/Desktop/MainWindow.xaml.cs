using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop;

public partial class MainWindow : Window
{
    private static readonly Guid ScheduleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly ObservableCollection<ActivityRow> _activity = new();
    private readonly ObservableCollection<Member> _members = new();
    private bool _scheduleLoaded;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        ActivityGrid.ItemsSource = _activity;
        MembersGrid.ItemsSource = _members;
        Loaded += async (_, _) =>
        {
            ServerText.Text = $"수신 대기: {App.Services.Server.ServerUrl}";
            await RefreshMembersAsync();
            await RefreshContactsAsync();
            var backfilled = await App.Services.Backups.BackfillRecordingMetadataAsync();
            if (backfilled > 0) StatusText.Text = $"기존 통화 녹음 {backfilled}개 파일명을 색인했습니다.";
            await RefreshFilesAsync();
            await RefreshGeneralFilesAsync();
            var schedules = await App.Services.Schedules.ListAsync();
            var schedule = schedules.FirstOrDefault();
            ScheduleTimesTextBox.Text = schedule?.Times.Length > 0 ? string.Join(", ", schedule.Times) : "19:00";
            _scheduleLoaded = true;
            ScheduleEnabledCheckBox.IsChecked = schedule?.Enabled == true;
            await RefreshLiveStatusAsync();
            _statusTimer.Tick += async (_, _) => await RefreshLiveStatusAsync();
            _statusTimer.Start();
        };
        Closed += (_, _) => _statusTimer.Stop();
    }

    private async void AddMember_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("새 멤버 이름", "멤버 추가");
        if (string.IsNullOrWhiteSpace(name)) return;
        var member = new Member(Guid.NewGuid(), name.Trim(), MemberStatus.Active, DateTimeOffset.Now);
        await App.Services.Database.ExecuteAsync("INSERT INTO members(id,name,status,created_at) VALUES($id,$name,$status,$at)", p => { p.AddWithValue("$id", member.Id.ToString()); p.AddWithValue("$name", member.Name); p.AddWithValue("$status", (int)member.Status); p.AddWithValue("$at", member.CreatedAt.ToString("O")); });
        _members.Add(member);
        MembersGrid.SelectedItem = member;
        StatusText.Text = $"멤버 ‘{member.Name}’가 추가되었습니다.";
    }

    private async Task RefreshMembersAsync()
    {
        var rows = await App.Services.Database.QueryAsync("SELECT id,name,status,created_at FROM members ORDER BY created_at", r => new Member(Guid.Parse(r.GetString(0)), r.GetString(1), (MemberStatus)r.GetInt32(2), DateTimeOffset.Parse(r.GetString(3))));
        _members.Clear(); foreach (var member in rows) _members.Add(member);
        DeviceCountText.Text = (await App.Services.Database.QueryAsync("SELECT COUNT(*) FROM devices WHERE status=1", r => r.GetInt32(0))).FirstOrDefault().ToString();
    }

    private async void ArchiveMember_Click(object sender, RoutedEventArgs e)
    {
        if (MembersGrid.SelectedItem is not Member member) return;
        if (MessageBox.Show($"‘{member.Name}’ 멤버를 삭제(보관)할까요? 기존 기기·파일·주소록 이력은 DB에 유지됩니다.", "멤버 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await App.Services.Database.ExecuteAsync("UPDATE members SET status=$status WHERE id=$id", p => { p.AddWithValue("$status", (int)MemberStatus.Archived); p.AddWithValue("$id", member.Id.ToString()); });
        await RefreshMembersAsync(); StatusText.Text = $"‘{member.Name}’ 멤버를 보관했습니다.";
    }

    private async void RestoreMember_Click(object sender, RoutedEventArgs e)
    {
        if (MembersGrid.SelectedItem is not Member member) return;
        await App.Services.Database.ExecuteAsync("UPDATE members SET status=$status WHERE id=$id", p => { p.AddWithValue("$status", (int)MemberStatus.Active); p.AddWithValue("$id", member.Id.ToString()); });
        await RefreshMembersAsync(); StatusText.Text = $"‘{member.Name}’ 멤버를 복원했습니다.";
    }

    private async void PairDevice_Click(object sender, RoutedEventArgs e)
    {
        var member = MembersGrid.SelectedItem as Member;
        if (member is null) { MessageBox.Show("멤버 목록에서 연결할 멤버를 먼저 선택하세요.", "기기 연결"); return; }
        if (member.Status == MemberStatus.Archived) { MessageBox.Show("보관된 멤버는 다시 활성화한 뒤 연결하세요.", "기기 연결"); return; }
        var ticket = await App.Services.Pairing.CreateTicketAsync(member.Id, App.Services.Server.ServerUrl);
        var window = new PairingWindow(ticket, member.Name) { Owner = this };
        window.ShowDialog();
        await RefreshMembersAsync();
    }

    private async void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLiveStatusAsync();
        StatusText.Text = "휴대폰에서 ‘지금 백업’을 누르면 이 화면에 진행률이 표시됩니다.";
        _activity.Insert(0, new ActivityRow(DateTime.Now, "선택 기기", "안내", "휴대폰의 ‘지금 백업’을 누르세요. PC가 자동으로 진행 상황을 표시합니다."));
    }

    private async Task RefreshLiveStatusAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var devices = await App.Services.Database.QueryAsync("SELECT display_name,last_seen_at FROM devices WHERE status=1 ORDER BY last_seen_at DESC", r => new { Name = r.GetString(0), LastSeen = r.IsDBNull(1) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(1)) });
            var connected = devices.Where(x => x.LastSeen is not null && now - x.LastSeen.Value < TimeSpan.FromSeconds(90)).ToList();
            ConnectionText.Text = connected.Count > 0 ? $"● PC 사용 가능: {string.Join(", ", connected.Select(x => x.Name))}" : devices.Count > 0 ? "● 기기 응답 대기 중 (휴대폰 앱을 열면 확인됩니다)" : "● 등록된 기기 없음";
            ConnectionText.Foreground = connected.Count > 0 ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.DarkGoldenrod;
            DeviceCountText.Text = $"{connected.Count}/{devices.Count}";

            var runs = await App.Services.Database.QueryAsync("""
                SELECT r.status,r.started_at,r.finished_at,r.files_seen,r.files_stored,r.error,
                       COALESCE(p.files_total,0),COALESCE(p.files_processed,0),p.current_file,COALESCE(p.stage,''),d.display_name
                FROM sync_runs r JOIN devices d ON d.id=r.device_id
                LEFT JOIN sync_progress p ON p.sync_run_id=r.id ORDER BY r.started_at DESC LIMIT 1
                """, r => new { Status = r.GetInt32(0), Started = DateTimeOffset.Parse(r.GetString(1)), Finished = r.IsDBNull(2) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(2)), Seen = r.GetInt32(3), Stored = r.GetInt32(4), Error = r.IsDBNull(5) ? null : r.GetString(5), Total = r.GetInt32(6), Processed = r.GetInt32(7), File = r.IsDBNull(8) ? null : r.GetString(8), Stage = r.GetString(9), Device = r.GetString(10) });
            var run = runs.FirstOrDefault();
            if (run is null) { BackupProgressBar.Value = 0; BackupProgressText.Text = "진행 중인 백업이 없습니다."; return; }
            BackupProgressBar.IsIndeterminate = run.Status == 0 && run.Total == 0;
            BackupProgressBar.Value = run.Total > 0 ? Math.Min(100, run.Processed * 100d / run.Total) : 0;
            BackupProgressText.Text = run.Status switch
            {
                0 => $"{run.Device} · {run.Stage} · {run.Processed}/{run.Total}{(run.Total > 0 ? $" ({run.Processed * 100 / run.Total}%)" : "")}{(string.IsNullOrWhiteSpace(run.File) ? "" : $" · {run.File}")}",
                1 => $"{run.Device} · 백업 완료 · {run.Stored}개 저장",
                _ => $"{run.Device} · 백업 실패 · {run.Error ?? "알 수 없는 오류"}"
            };
            if (run.Finished is not null) LastBackupText.Text = run.Finished.Value.ToLocalTime().ToString("MM-dd HH:mm");
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) await RefreshFilesAsync();
            if (string.IsNullOrWhiteSpace(GeneralSearchBox.Text)) await RefreshGeneralFilesAsync();
        }
        catch (Exception ex) { BackupProgressText.Text = $"상태 확인 실패: {ex.Message}"; }
    }

    private async void ScheduleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_scheduleLoaded) return;
        await SaveScheduleAsync(ScheduleEnabledCheckBox.IsChecked == true);
    }

    private async void SaveScheduleTime_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseScheduleTimes(ScheduleTimesTextBox.Text, out var times, out var error))
        {
            MessageBox.Show(error, "예약 백업 시각", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ScheduleTimesTextBox.Text = string.Join(", ", times);
        await SaveScheduleAsync(ScheduleEnabledCheckBox.IsChecked == true, times);
        StatusText.Text = $"예약 백업 시각을 {string.Join(", ", times)}(으)로 저장했습니다.";
    }

    private async Task SaveScheduleAsync(bool enabled, string[]? times = null)
    {
        if (times is null && !TryParseScheduleTimes(ScheduleTimesTextBox.Text, out times, out _)) times = ["19:00"];
        var existing = (await App.Services.Schedules.ListAsync()).FirstOrDefault(x => x.Id == ScheduleId);
        var schedule = new Services.BackupSchedule(ScheduleId, existing?.DeviceId, existing?.Category ?? "all", enabled,
            existing?.Weekdays is { Length: > 0 } weekdays ? weekdays : [1, 2, 3, 4, 5, 6, 7], times!, existing?.LastOccurrence);
        await App.Services.Schedules.SaveAsync(schedule);
        StatusText.Text = enabled ? "예약 백업을 활성화했습니다." : "예약 백업을 비활성화했습니다.";
    }

    private static bool TryParseScheduleTimes(string value, out string[] times, out string error)
    {
        var values = value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<string>();
        foreach (var item in values)
        {
            if (!TimeOnly.TryParseExact(item, ["H:mm", "HH:mm"], out var time))
            {
                times = []; error = $"‘{item}’은 올바른 시각이 아닙니다. 예: 09:30 또는 19:00"; return false;
            }
            parsed.Add(time.ToString("HH:mm"));
        }
        times = parsed.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        error = times.Length == 0 ? "최소 한 개의 백업 시각을 입력하세요." : "";
        return times.Length > 0;
    }

    private async void RefreshContacts_Click(object sender, RoutedEventArgs e) => await RefreshContactsAsync();
    private async Task RefreshContactsAsync()
    {
        try { ContactsGrid.ItemsSource = await App.Services.Contacts.ListAsync(); }
        catch (Exception ex) { StatusText.Text = $"주소록을 불러오지 못했습니다: {ex.Message}"; }
    }

    private async void MembersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MembersGrid.SelectedItem is Member member)
        {
            DevicesGrid.ItemsSource = await App.Services.Database.QueryAsync(
                "SELECT id,member_id,display_name,platform,model,android_version,status,last_seen_at,token_hash FROM devices WHERE member_id=$member ORDER BY last_seen_at DESC",
                r => new Device(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), (DeviceStatus)r.GetInt32(6), r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)), r.GetString(8)),
                p => p.AddWithValue("$member", member.Id.ToString()));
        }
        else DevicesGrid.ItemsSource = null;
    }

    private async void AddContact_Click(object sender, RoutedEventArgs e) => await EditContactAsync(null);
    private async void EditContact_Click(object sender, RoutedEventArgs e) => await EditContactAsync(ContactsGrid.SelectedItem as Contact);
    private async void DeleteContact_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsGrid.SelectedItem is not Contact contact) return;
        if (MessageBox.Show($"‘{contact.DisplayName}’ 연락처를 PC 통합본에서 삭제할까요?", "주소록 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await App.Services.Contacts.DeleteAsync(contact.Id); await RefreshContactsAsync();
    }
    private async Task EditContactAsync(Contact? existing)
    {
        var name = Prompt("이름", existing?.DisplayName ?? ""); if (string.IsNullOrWhiteSpace(name)) return;
        var phone = Prompt("전화번호(여러 개는 쉼표로 구분)", existing is null ? "" : string.Join(",", existing.PhoneNumbers)) ?? "";
        var email = Prompt("이메일(여러 개는 쉼표로 구분)", existing is null ? "" : string.Join(",", existing.Emails)) ?? "";
        var contact = new Contact(existing?.Id ?? Guid.NewGuid(), name.Trim(), phone.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), email.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), existing?.Company, existing?.Notes, DateTimeOffset.Now);
        await App.Services.Contacts.SaveAsync(contact); await RefreshContactsAsync(); StatusText.Text = "주소록을 저장했습니다.";
    }
    private async void ExportContacts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 파일|*.csv", FileName = "업무통합주소록.csv" }; if (dialog.ShowDialog() != true) return;
        var contacts = await App.Services.Contacts.ListAsync(); await File.WriteAllLinesAsync(dialog.FileName, new[] { "이름,전화번호,이메일,회사" }.Concat(contacts.Select(c => $"\"{c.DisplayName.Replace("\"", "\"\"")}\",\"{string.Join(";", c.PhoneNumbers)}\",\"{string.Join(";", c.Emails)}\",\"{c.Company}\"")), System.Text.Encoding.UTF8); StatusText.Text = "CSV를 내보냈습니다.";
    }
    private async void SearchFiles_Click(object sender, RoutedEventArgs e)
    {
        await RefreshFilesAsync(SearchBox.Text.Trim());
    }
    private async void SearchGeneralFiles_Click(object sender, RoutedEventArgs e)
    {
        await RefreshGeneralFilesAsync(GeneralSearchBox.Text.Trim());
    }
    private async Task RefreshFilesAsync(string term = "")
    {
        var rows = await App.Services.Database.QueryAsync("""
            SELECT COALESCE(m.name,'미등록') AS member_name, b.category, b.recorded_at,
                   COALESCE(b.parsed_contact_name, (SELECT c.display_name FROM contacts c
                     JOIN contact_phones cp ON cp.contact_id=c.id
                     WHERE c.deleted_at IS NULL AND cp.normalized=b.parsed_phone_number LIMIT 1)),
                   b.original_file_name, b.verified_at
            FROM backup_items b
            JOIN devices d ON d.id=b.device_id
            LEFT JOIN members m ON m.id=d.member_id
            WHERE b.category='recording'
              AND ($term='' OR b.original_file_name LIKE '%'||$term||'%'
                   OR b.parsed_contact_name LIKE '%'||$term||'%'
                   OR b.parsed_phone_number LIKE '%'||$term||'%')
            ORDER BY COALESCE(b.recorded_at,b.last_seen_at) DESC LIMIT 300
            """, r => new { MemberName = r.GetString(0), Category = CategoryLabel(r.GetString(1)), RecordedAt = r.IsDBNull(2) ? "미확인" : r.GetString(2), ParsedContactName = r.IsDBNull(3) ? "미확인" : r.GetString(3), OriginalFileName = r.GetString(4), Status = r.IsDBNull(5) ? "대기" : "검증 완료" }, p => p.AddWithValue("$term", term));
        RecordingGrid.ItemsSource = rows;
    }

    private async Task RefreshGeneralFilesAsync(string term = "")
    {
        var rows = await App.Services.Database.QueryAsync("""
            SELECT COALESCE(m.name,'미등록') AS member_name, b.category, b.last_modified_at,
                   b.original_file_name, b.verified_at
            FROM backup_items b
            JOIN devices d ON d.id=b.device_id
            LEFT JOIN members m ON m.id=d.member_id
            WHERE b.category<>'recording'
              AND ($term='' OR b.original_file_name LIKE '%'||$term||'%' OR b.relative_path LIKE '%'||$term||'%')
            ORDER BY b.last_modified_at DESC LIMIT 300
            """, r => new { MemberName = r.GetString(0), Category = CategoryLabel(r.GetString(1)), LastModifiedAt = r.IsDBNull(2) ? "미확인" : r.GetString(2), OriginalFileName = r.GetString(3), Status = r.IsDBNull(4) ? "대기" : "검증 완료" }, p => p.AddWithValue("$term", term));
        FilesGrid.ItemsSource = rows;
    }
    private static string CategoryLabel(string category) => category switch { "recording" => "통화녹음", "image" => "사진", "video" => "영상", "document" => "문서", "audio" => "음성", _ => "기타" };
    private void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "백업 저장 폴더 선택" };
        if (dialog.ShowDialog() == true) { App.Services.Backups.Root = dialog.FolderName; StatusText.Text = $"백업 폴더: {dialog.FolderName}"; }
    }

    private static string? Prompt(string message, string title)
    {
        var dialog = new TextPromptWindow(message, title) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public sealed record ActivityRow(DateTime Time, string Target, string Status, string Message);
}
