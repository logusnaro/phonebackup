using System.IO;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using PhoneBackup.Desktop.Models;
using PhoneBackup.Desktop.Services;

namespace PhoneBackup.Desktop;

public partial class MainWindow : Window
{
    private static readonly Guid LegacyScheduleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
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
            try
            {
                ServerText.Text = $"수신 대기: {App.Services.Server.ServerUrl}";
                await RefreshMembersAsync();
                await RefreshScheduleDevicesAsync();
                await RefreshContactsAsync();
                var backfilled = await App.Services.Backups.BackfillRecordingMetadataAsync();
                if (backfilled > 0) StatusText.Text = $"기존 통화 녹음 {backfilled}개 파일명을 색인했습니다.";
                await RefreshFilesAsync();
                await RefreshGeneralFilesAsync();
                await RefreshDeletionCountAsync();
                await LoadScheduleForSelectedDeviceAsync();
                await RefreshLiveStatusAsync();
                _statusTimer.Tick += async (_, _) => await RefreshLiveStatusAsync();
                _statusTimer.Start();
            }
            catch (Exception ex)
            {
                App.LogCrash("MainWindow.Loaded", ex);
                StatusText.Text = $"화면 초기화 실패: {ex.Message} · 로그: %TEMP%\\PhoneBackup-crash.log";
            }
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
        ContactMemberComboBox.ItemsSource = _members;
        if (ContactMemberComboBox.SelectedIndex < 0 && _members.Count > 0) ContactMemberComboBox.SelectedIndex = 0;
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
        await RefreshScheduleDevicesAsync();
    }

    private void OpenSmartSwitch_Click(object sender, RoutedEventArgs e)
    {
        var opened = App.Services.SmartSwitch.TryOpenApplication(out var message);
        StatusText.Text = message;
        if (!opened) MessageBox.Show(message, "Smart Switch", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void AutoDiscoverSmartSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (MembersGrid.SelectedItem is not Member member)
        {
            MessageBox.Show("멤버·기기 탭에서 백업 주인인 멤버를 먼저 선택하세요.", "백업 자동 찾기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            BackupProgressBar.IsIndeterminate = true;
            BackupProgressText.Text = "Smart Switch 백업 위치를 자동으로 찾는 중…";
            var scanProgress = new Progress<SmartSwitchDiscoveryProgress>(value =>
            {
                BackupProgressText.Text = $"백업 위치 검색 중 · {value.LocationsScanned}곳 확인 · {value.CandidatesFound}개 발견";
                StatusText.Text = $"검색 중: {value.Location}";
            });
            var candidates = await App.Services.SmartSwitchDiscovery.DiscoverDefaultAsync(scanProgress);
            if (candidates.Count == 0)
            {
                BackupProgressText.Text = "자동으로 찾은 Smart Switch 백업이 없습니다.";
                MessageBox.Show(
                    "Windows 설정·문서·바탕 화면·OneDrive와 이전 등록 위치를 확인했지만 백업 파일을 찾지 못했습니다.\n\nSmart Switch에서 백업을 끝낸 뒤 다시 시도하거나 ‘폴더 직접 가져오기’로 백업의 상위 폴더를 한 번 지정하세요.",
                    "백업 자동 찾기", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await ImportSmartSwitchCandidatesAsync(member, candidates);
        }
        catch (Exception ex)
        {
            App.LogCrash("AutoDiscoverSmartSwitch", ex);
            StatusText.Text = $"백업 자동 검색 실패: {ex.Message}";
            MessageBox.Show(StatusText.Text, "백업 자동 찾기", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BackupProgressBar.IsIndeterminate = false;
        }
    }

    private async void ImportSmartSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (MembersGrid.SelectedItem is not Member member)
        {
            MessageBox.Show("멤버·기기 탭에서 백업을 등록할 멤버를 먼저 선택하세요.", "백업 자동 찾기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Smart Switch 백업 폴더 또는 그 상위 폴더 선택" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            BackupProgressBar.IsIndeterminate = true;
            var candidates = await App.Services.SmartSwitchDiscovery.DiscoverSelectedAsync(dialog.FolderName);
            BackupProgressBar.IsIndeterminate = false;
            if (candidates.Count == 0)
            {
                MessageBox.Show(
                    "선택한 폴더에서 PB가 관리할 통화녹음·사진·영상·문서 파일을 찾지 못했습니다.\n\n.spbm만 있다면 연락처 전용 백업이므로 Smart Switch에서 복원할 수 있지만 PB 파일 목록에는 등록되지 않습니다.",
                    "가져올 파일 없음", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await ImportSmartSwitchCandidatesAsync(member, candidates);
        }
        catch (Exception ex)
        {
            App.LogCrash("ImportSmartSwitch", ex);
            StatusText.Text = $"Smart Switch 백업 찾기 실패: {ex.Message}";
            MessageBox.Show(StatusText.Text, "백업 자동 찾기", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { BackupProgressBar.IsIndeterminate = false; }
    }

    private async Task ImportSmartSwitchCandidatesAsync(Member member, IReadOnlyList<SmartSwitchBackupCandidate> candidates)
    {
        var preview = string.Join("\n", candidates.Take(10).Select(candidate => $"• {candidate.Summary}"));
        if (candidates.Count > 10) preview += $"\n• 그 외 {candidates.Count - 10}개";
        if (MessageBox.Show(
                $"‘{member.Name}’ 멤버의 백업 {candidates.Count}개를 찾았습니다.\n\n{preview}\n\n" +
                "PB는 원본을 수정하지 않고 파일별 SHA-256 검증 후 자체 저장소에 복사합니다. " +
                "모델이 일치하는 등록 휴대폰만 모바일 삭제 기능과 연결되며, 나머지는 PC 보관 전용으로 등록됩니다.\n\n계속하시겠습니까?",
                "Smart Switch 백업 등록", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var outcomes = new List<string>();
        var anyFailure = false;
        foreach (var (candidate, candidateIndex) in candidates.Select((value, index) => (value, index)))
        {
            var importProgress = new Progress<SmartSwitchImportProgress>(value =>
            {
                BackupProgressBar.IsIndeterminate = false;
                BackupProgressBar.Maximum = Math.Max(1, value.Total);
                BackupProgressBar.Value = value.Processed;
                BackupProgressText.Text = $"[{candidateIndex + 1}/{candidates.Count}] {candidate.Model} 가져오기 · {value.Processed}/{value.Total} · 저장 {value.Stored} · 실패 {value.Failed} · {value.CurrentFile}";
                StatusText.Text = BackupProgressText.Text;
            });
            try
            {
                var imported = await App.Services.SmartSwitchImport.ImportAsync(member.Id, candidate.RootPath, importProgress);
                var verifyProgress = new Progress<SmartSwitchVerificationProgress>(value =>
                {
                    BackupProgressBar.Maximum = Math.Max(1, value.Total);
                    BackupProgressBar.Value = value.Processed;
                    BackupProgressText.Text = $"[{candidateIndex + 1}/{candidates.Count}] {candidate.Model} 검증 · {value.Processed}/{value.Total} · {value.CurrentFile}";
                });
                var verification = await App.Services.SmartSwitch.VerifyAsync(imported.DeviceId, candidate.RootPath, verifyProgress);
                var link = imported.LinkedToPairedPhone ? "등록 휴대폰과 연결됨" : "PC 보관 전용";
                var warning = verification.Warnings.Count == 0 ? string.Empty : $" · 안내 {verification.Warnings.Count}건";
                outcomes.Add($"{candidate.Model}: 저장 {imported.Stored}, 기존 {imported.Skipped}, 실패 {imported.Failed} · 검증 {verification.VerifiedFiles}/{verification.TotalFiles} · {link}{warning}");
                if (imported.Failed > 0 || !verification.Success)
                {
                    anyFailure = true;
                    foreach (var error in imported.Errors.Concat(verification.Errors).Take(5)) outcomes.Add($"  - {error}");
                }
            }
            catch (Exception ex)
            {
                anyFailure = true;
                App.LogCrash("ImportSmartSwitchCandidate", ex);
                outcomes.Add($"{candidate.Model}: 등록 실패 · {ex.Message}");
            }
        }

        await RefreshMembersAsync();
        await RefreshFilesAsync();
        await RefreshGeneralFilesAsync();
        await RefreshDeletionCountAsync();
        await RefreshLiveStatusAsync();
        BackupProgressBar.Value = BackupProgressBar.Maximum;
        BackupProgressText.Text = anyFailure ? "Smart Switch 등록 완료 · 일부 항목 확인 필요" : "Smart Switch 등록 및 검증 완료";
        StatusText.Text = BackupProgressText.Text;
        MessageBox.Show(
            $"Smart Switch 백업 처리가 끝났습니다.\n\n{string.Join("\n", outcomes.Take(30))}\n\n원본 Smart Switch 백업은 그대로 유지됩니다. 검증이 끝나지 않은 파일은 모바일 삭제 대상이 되지 않습니다.",
            anyFailure ? "Smart Switch 처리 결과 확인" : "Smart Switch 처리 완료",
            MessageBoxButton.OK, anyFailure ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScheduleDeviceId() is not Guid deviceId)
        {
            StatusText.Text = "설정에서 대상 휴대폰을 먼저 선택하세요.";
            return;
        }
        await QueueBackupRequestAsync(deviceId);
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
            await RefreshDeletionCountAsync();
        }
        catch (Exception ex) { BackupProgressText.Text = $"상태 확인 실패: {ex.Message}"; }
    }

    private async void ScheduleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_scheduleLoaded) return;
        await SaveScheduleAsync(ScheduleEnabledCheckBox.IsChecked == true);
    }

    private async void ScheduleDeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_scheduleLoaded) return;
        await LoadScheduleForSelectedDeviceAsync();
    }

    private async Task RefreshScheduleDevicesAsync()
    {
        var rows = await App.Services.Database.QueryAsync("""
            SELECT d.id, COALESCE(m.name,'미등록') || ' · ' || d.display_name ||
                   CASE WHEN d.model IS NULL OR d.model='' THEN '' ELSE ' (' || d.model || ')' END
            FROM devices d LEFT JOIN members m ON m.id=d.member_id
            WHERE d.status<>3 ORDER BY COALESCE(m.name,''),d.display_name
            """, r => new DeviceChoice(Guid.Parse(r.GetString(0)), r.GetString(1)));
        _scheduleLoaded = false;
        ScheduleDeviceComboBox.ItemsSource = rows;
        ScheduleDeviceComboBox.SelectedIndex = rows.Count == 0 ? -1 : 0;
        _scheduleLoaded = true;
        await LoadScheduleForSelectedDeviceAsync();
    }

    private Guid? SelectedScheduleDeviceId() => ScheduleDeviceComboBox.SelectedValue is Guid id ? id :
        ScheduleDeviceComboBox.SelectedItem is DeviceChoice choice ? choice.Id : null;

    private async Task LoadScheduleForSelectedDeviceAsync()
    {
        _scheduleLoaded = false;
        var deviceId = SelectedScheduleDeviceId();
        if (deviceId is null)
        {
            ScheduleEnabledCheckBox.IsChecked = false;
            ScheduleEnabledCheckBox.IsEnabled = false;
            ScheduleTimesTextBox.Text = "19:00";
            ScheduleTimesTextBox.IsEnabled = false;
            _scheduleLoaded = true;
            return;
        }
        ScheduleEnabledCheckBox.IsEnabled = true;
        ScheduleTimesTextBox.IsEnabled = true;
        var schedules = await App.Services.Schedules.ListAsync();
        var schedule = schedules.FirstOrDefault(x => x.DeviceId == deviceId) ?? schedules.FirstOrDefault(x => x.Id == LegacyScheduleId && x.DeviceId is null);
        ScheduleTimesTextBox.Text = schedule?.Times.Length > 0 ? string.Join(", ", schedule.Times) : "19:00";
        ScheduleEnabledCheckBox.IsChecked = schedule?.Enabled == true;
        _scheduleLoaded = true;
    }

    private async void ManualBackupForSelectedDevice_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScheduleDeviceId() is not Guid deviceId)
        {
            StatusText.Text = "연결된 휴대폰이 없습니다. 먼저 기기를 연결하세요.";
            return;
        }
        await QueueBackupRequestAsync(deviceId);
    }

    private async Task QueueBackupRequestAsync(Guid deviceId)
    {
        var label = await App.Services.Database.QueryAsync("SELECT display_name FROM devices WHERE id=$id", r => r.GetString(0), p => p.AddWithValue("$id", deviceId.ToString()));
        if (label.Count == 0) { StatusText.Text = "대상 휴대폰을 찾을 수 없습니다."; return; }
        var requestId = Guid.NewGuid();
        await App.Services.Database.ExecuteAsync("INSERT INTO backup_requests(id,device_id,status,created_at) VALUES($id,$device,0,$at)", p =>
        {
            p.AddWithValue("$id", requestId.ToString()); p.AddWithValue("$device", deviceId.ToString()); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        });
        StatusText.Text = $"{label[0]}에 백업 요청을 보냈습니다. 휴대폰 앱이 Wi‑Fi로 연결되면 자동으로 시작합니다.";
        _activity.Insert(0, new ActivityRow(DateTime.Now, label[0], "백업 요청", "PC에서 백업을 요청했습니다. 휴대폰 앱이 자동으로 시작합니다."));
        await RefreshLiveStatusAsync();
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

    private async void RefreshDeletionCandidates_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDeletionCountAsync();
        StatusText.Text = "휴대폰의 ‘90일 이전 삭제’ 버튼을 누르면 해시 검증 가능한 삭제 후보가 표시됩니다.";
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PB 오류 리포트|PB-error-report-*.zip",
            FileName = $"PB-error-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await App.Services.Diagnostics.ExportAsync(dialog.FileName);
            StatusText.Text = $"오류 리포트를 저장했습니다: {dialog.FileName}";
            MessageBox.Show("오류 리포트를 저장했습니다. 파일을 Codex 대화에 첨부해 ‘PB 오류 분석’이라고 남겨 주세요. 파일 내용·연락처·인증정보는 포함하지 않습니다.", "오류 리포트", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            App.LogCrash("ExportDiagnostics", ex);
            StatusText.Text = $"오류 리포트 저장 실패: {ex.Message}";
            MessageBox.Show(StatusText.Text, "오류 리포트", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveScheduleAsync(bool enabled, string[]? times = null)
    {
        if (times is null && !TryParseScheduleTimes(ScheduleTimesTextBox.Text, out times, out _)) times = ["19:00"];
        if (SelectedScheduleDeviceId() is not Guid deviceId)
        {
            StatusText.Text = "설정할 휴대폰을 먼저 선택하세요.";
            return;
        }
        var existing = (await App.Services.Schedules.ListAsync()).FirstOrDefault(x => x.DeviceId == deviceId);
        var schedule = new Services.BackupSchedule(existing?.Id ?? deviceId, deviceId, existing?.Category ?? "all", enabled,
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
        try
        {
            var memberId = (ContactMemberComboBox.SelectedItem as Member)?.Id;
            ContactsGrid.ItemsSource = memberId is null ? Array.Empty<Contact>() : await App.Services.Contacts.ListAsync(memberId);
        }
        catch (Exception ex) { StatusText.Text = $"주소록을 불러오지 못했습니다: {ex.Message}"; }
    }

    private async void ContactMemberComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => await RefreshContactsAsync();

    private async void MembersGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MembersGrid.SelectedItem is Member member)
        {
            ContactMemberComboBox.SelectedItem = member;
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
        var memberId = existing?.MemberId ?? (ContactMemberComboBox.SelectedItem as Member)?.Id;
        if (memberId is null)
        {
            MessageBox.Show("연락처 메모를 저장할 개인 프로필을 먼저 선택하세요.", "연락처 메모");
            return;
        }
        var contact = new Contact(existing?.Id ?? Guid.NewGuid(), name.Trim(), phone.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), email.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), existing?.Company, existing?.Notes, DateTimeOffset.Now, memberId);
        await App.Services.Contacts.SaveAsync(contact); await RefreshContactsAsync(); StatusText.Text = "주소록을 저장했습니다.";
    }
    private async void ExportContacts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 파일|*.csv", FileName = "업무통합주소록.csv" }; if (dialog.ShowDialog() != true) return;
        var memberId = (ContactMemberComboBox.SelectedItem as Member)?.Id;
        if (memberId is null) { MessageBox.Show("내보낼 개인 프로필을 먼저 선택하세요.", "CSV 내보내기"); return; }
        var contacts = await App.Services.Contacts.ListAsync(memberId); await File.WriteAllLinesAsync(dialog.FileName, new[] { "이름,전화번호,이메일,회사" }.Concat(contacts.Select(c => $"\"{c.DisplayName.Replace("\"", "\"\"")}\",\"{string.Join(";", c.PhoneNumbers)}\",\"{string.Join(";", c.Emails)}\",\"{c.Company}\"")), System.Text.Encoding.UTF8); StatusText.Text = "CSV를 내보냈습니다.";
    }
    private async void SearchFiles_Click(object sender, RoutedEventArgs e)
    {
        await RefreshFilesAsync(SearchBox.Text.Trim());
    }
    private async void SearchGeneralFiles_Click(object sender, RoutedEventArgs e)
    {
        await RefreshGeneralFilesAsync(GeneralSearchBox.Text.Trim());
    }

    private async void DeleteMobile_Click(object sender, RoutedEventArgs e)
    {
        var grid = sender is System.Windows.Controls.Button button && button.Tag is string tag && tag == "general" ? FilesGrid : RecordingGrid;
        var selected = GetSelectedRows(grid);
        if (selected.Count == 0) { MessageBox.Show("삭제할 파일을 먼저 선택하세요.", "모바일 삭제"); return; }
        if (MessageBox.Show($"[최종 경고]\n\n선택한 {selected.Count}개 파일의 휴대폰 원본 삭제를 요청합니다.\nPC 백업본은 유지되지만, 휴대폰 파일은 해시 확인 후 삭제되며 복구할 수 없습니다.\n\n계속하시겠습니까?", "모바일 파일 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var queued = 0;
        var deletionCutoff = DateTimeOffset.UtcNow.AddDays(-90).ToString("O");
        foreach (var deviceGroup in selected.GroupBy(x => x.DeviceId))
        {
            var items = new List<object>();
            foreach (var row in deviceGroup)
            {
                var verified = await App.Services.Database.QueryAsync("""
                    SELECT 1 FROM backup_items b JOIN devices d ON d.id=b.device_id
                    WHERE b.id=$id AND b.device_id=$device AND b.sha256=$sha
                      AND d.platform='Android' AND d.status=1
                      AND b.category='recording' AND b.verified_at IS NOT NULL
                      AND COALESCE(b.recorded_at,b.last_modified_at) <= $cutoff
                      AND EXISTS (SELECT 1 FROM smart_switch_backups s
                                  WHERE s.device_id=b.device_id AND s.status=1 AND s.verified_at IS NOT NULL
                                    AND s.completed_at >= COALESCE(b.recorded_at,b.last_modified_at))
                    LIMIT 1
                    """, _ => true,
                    p => { p.AddWithValue("$id", row.BackupItemId); p.AddWithValue("$device", row.DeviceId); p.AddWithValue("$sha", row.Sha256); p.AddWithValue("$cutoff", deletionCutoff); });
                if (verified.Count > 0) items.Add(new { id = row.BackupItemId, relativePath = row.RelativePath, sha256 = row.Sha256 });
            }
            if (items.Count == 0) continue;
            await App.Services.Database.ExecuteAsync("INSERT INTO deletion_requests(id,device_id,items_json,status,created_at) VALUES($id,$device,$items,0,$at)", p =>
            {
                p.AddWithValue("$id", Guid.NewGuid().ToString()); p.AddWithValue("$device", deviceGroup.Key);
                p.AddWithValue("$items", System.Text.Json.JsonSerializer.Serialize(items)); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            });
            queued += items.Count;
        }
        StatusText.Text = queued == 0
            ? "삭제 요청 없음: 90일 경과·통화녹음·해시 검증·동일 모델의 등록 휴대폰 연결 조건을 모두 확인하세요. 일반 파일과 PC 보관 전용 기기는 모바일에서 삭제하지 않습니다."
            : $"모바일 삭제 {queued}개를 요청했습니다. 휴대폰 앱이 연결되면 원본 경로와 해시를 재검사한 뒤 삭제합니다.";
    }

    private async void DeleteLocal_Click(object sender, RoutedEventArgs e)
    {
        var grid = sender is System.Windows.Controls.Button button && button.Tag is string tag && tag == "general" ? FilesGrid : RecordingGrid;
        var selected = GetSelectedRows(grid);
        if (selected.Count == 0) { MessageBox.Show("삭제할 파일을 먼저 선택하세요.", "로컬 삭제"); return; }
        if (MessageBox.Show($"[최종 경고]\n\n선택한 {selected.Count}개 파일의 PC 백업본을 영구 삭제합니다.\n휴대폰 원본에는 영향이 없지만, 이 PC의 백업 파일과 목록은 삭제되며 복구할 수 없습니다.\n\n계속하시겠습니까?", "PC 로컬 파일 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var deleted = 0;
        foreach (var row in selected)
        {
            if (!TryResolveBackupPath(row, out var presentationPath)) continue;
            try
            {
                if (File.Exists(presentationPath)) File.Delete(presentationPath);
                var stored = await App.Services.Database.QueryAsync("""
                    SELECT b.stored_object_id,o.storage_path,
                           (SELECT COUNT(*) FROM backup_items x WHERE x.stored_object_id=b.stored_object_id)
                    FROM backup_items b JOIN stored_objects o ON o.id=b.stored_object_id WHERE b.id=$id LIMIT 1
                    """, r => new { ObjectId = r.GetString(0), StoragePath = r.GetString(1), References = r.GetInt32(2) }, p => p.AddWithValue("$id", row.BackupItemId));
                await App.Services.Database.ExecuteAsync("DELETE FROM deletion_candidates WHERE id=$id; DELETE FROM backup_items WHERE id=$id;", p => p.AddWithValue("$id", row.BackupItemId));
                if (stored.Count > 0 && stored[0].References <= 1)
                {
                    if (File.Exists(stored[0].StoragePath)) File.Delete(stored[0].StoragePath);
                    await App.Services.Database.ExecuteAsync("DELETE FROM stored_objects WHERE id=$id", p => p.AddWithValue("$id", stored[0].ObjectId));
                }
                await App.Services.Database.ExecuteAsync("INSERT INTO audit_log(event_type,subject_id,details_json,created_at) VALUES('file_deleted_on_pc',$id,$details,$at)", p =>
                {
                    p.AddWithValue("$id", row.BackupItemId); p.AddWithValue("$details", System.Text.Json.JsonSerializer.Serialize(new { row.RelativePath, row.OriginalFileName })); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                });
                deleted++;
            }
            catch (Exception ex) { _activity.Insert(0, new ActivityRow(DateTime.Now, row.MemberName, "삭제 실패", ex.Message)); }
        }
        await RefreshFilesAsync(SearchBox.Text.Trim()); await RefreshGeneralFilesAsync(GeneralSearchBox.Text.Trim()); await RefreshDeletionCountAsync();
        StatusText.Text = $"PC 로컬 백업본 {deleted}개를 삭제했습니다. 휴대폰 원본은 유지됩니다.";
    }

    private static List<FileRow> GetSelectedRows(System.Windows.Controls.DataGrid grid)
        => grid.SelectedItems.OfType<FileRow>().Distinct().ToList();

    private async Task RefreshFilesAsync(string term = "")
    {
        var rows = await App.Services.Database.QueryAsync("""
            SELECT b.id,b.device_id,d.member_id, b.relative_path, COALESCE(m.name,'미등록') AS member_name, b.category, b.recorded_at,
                   b.parsed_target,
                     COALESCE(b.parsed_contact_name, (SELECT c.display_name FROM contacts c
                     JOIN contact_phones cp ON cp.contact_id=c.id
                     WHERE c.deleted_at IS NULL AND c.member_id=d.member_id AND cp.normalized=b.parsed_phone_number LIMIT 1)),
                   b.parsed_affiliation,b.original_file_name, b.verified_at,b.sha256
            FROM backup_items b
            JOIN devices d ON d.id=b.device_id
            LEFT JOIN members m ON m.id=d.member_id
            WHERE b.category='recording'
              AND ($term='' OR b.original_file_name LIKE '%'||$term||'%'
                   OR b.parsed_contact_name LIKE '%'||$term||'%'
                   OR b.parsed_phone_number LIKE '%'||$term||'%')
            ORDER BY COALESCE(b.recorded_at,b.last_seen_at) DESC LIMIT 300
            """, r => new FileRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), CategoryLabel(r.GetString(5)), r.IsDBNull(6) ? "미확인" : r.GetString(6), r.IsDBNull(7) ? "미확인" : r.GetString(7), r.IsDBNull(8) ? "미확인" : r.GetString(8), r.IsDBNull(9) ? "미확인" : r.GetString(9), r.GetString(10), r.IsDBNull(11) ? "대기" : "검증 완료", r.GetString(12)), p => p.AddWithValue("$term", term));
        RecordingGrid.ItemsSource = rows;
    }

    private async Task RefreshGeneralFilesAsync(string term = "")
    {
        var rows = await App.Services.Database.QueryAsync("""
            SELECT b.id,b.device_id,d.member_id, b.relative_path, COALESCE(m.name,'미등록') AS member_name, b.category, b.last_modified_at,
                   b.original_file_name, b.verified_at,b.sha256
            FROM backup_items b
            JOIN devices d ON d.id=b.device_id
            LEFT JOIN members m ON m.id=d.member_id
            WHERE b.category<>'recording'
              AND ($term='' OR b.original_file_name LIKE '%'||$term||'%' OR b.relative_path LIKE '%'||$term||'%')
            ORDER BY b.last_modified_at DESC LIMIT 300
            """, r => new FileRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), CategoryLabel(r.GetString(5)), null, "미확인", "미확인", "미확인", r.GetString(7), r.IsDBNull(8) ? "대기" : "검증 완료", r.GetString(9)), p => p.AddWithValue("$term", term));
        FilesGrid.ItemsSource = rows;
    }
    private static string CategoryLabel(string category) => category switch { "recording" => "통화녹음", "image" => "사진", "video" => "영상", "document" => "문서", "audio" => "음성", _ => "기타" };

    private void FileGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.Controls.DataGrid)?.SelectedItem is FileRow row) OpenFileOrFolder(row);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: FileRow row }) OpenFolder(row);
    }

    private void OpenFileOrFolder(FileRow row)
    {
        if (!TryResolveBackupPath(row, out var path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                StatusText.Text = $"파일을 실행했습니다: {row.OriginalFileName}";
            }
            else
            {
                var folder = Directory.Exists(Path.GetDirectoryName(path)) ? Path.GetDirectoryName(path)! : App.Services.Backups.Root;
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                StatusText.Text = $"백업 파일을 찾지 못해 폴더를 열었습니다: {folder}";
            }
        }
        catch (Exception ex) { StatusText.Text = $"파일을 열지 못했습니다: {ex.Message}"; }
    }

    private void OpenFolder(FileRow row)
    {
        if (!TryResolveBackupPath(row, out var path)) return;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder)) throw new DirectoryNotFoundException("파일 폴더를 찾을 수 없습니다.");
            Directory.CreateDirectory(folder);
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            StatusText.Text = File.Exists(path) ? $"파일 위치를 열었습니다: {row.OriginalFileName}" : $"파일 폴더를 열었습니다: {folder}";
        }
        catch (Exception ex) { StatusText.Text = $"폴더를 열지 못했습니다: {ex.Message}"; }
    }

    private bool TryResolveBackupPath(FileRow row, out string path)
    {
        try
        {
            var relative = BackupService.NormalizeRelativePath(row.RelativePath);
            var legacy = Path.Combine(App.Services.Backups.Root, "members", row.MemberId, "devices", row.DeviceId, relative);
            var versioned = Path.Combine(App.Services.Backups.Root, "members", row.MemberId, "devices", row.DeviceId, "versions", row.Sha256, relative);
            path = File.Exists(versioned) || !File.Exists(legacy) ? versioned : legacy;
            return true;
        }
        catch (Exception ex)
        {
            path = string.Empty;
            StatusText.Text = $"파일 경로를 열 수 없습니다: {ex.Message}";
            return false;
        }
    }

    private sealed record FileRow(string BackupItemId, string DeviceId, string MemberId, string RelativePath, string MemberName,
        string Category, string? RecordedAt, string? ParsedTarget, string ParsedContactName,
        string ParsedAffiliation, string OriginalFileName, string Status, string Sha256);
    private async Task RefreshDeletionCountAsync()
    {
        var count = await App.Services.Database.QueryAsync("SELECT COUNT(*) FROM deletion_candidates WHERE approved_at IS NULL AND eligible_at <= $now", r => r.GetInt32(0), p => p.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")));
        DeletionCountText.Text = count.FirstOrDefault().ToString();
    }
    private async void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "백업 저장 폴더 선택" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await App.Services.Backups.SetRootAsync(dialog.FolderName);
                StatusText.Text = $"백업 폴더를 저장했습니다: {dialog.FolderName}";
            }
            catch (Exception ex)
            {
                App.LogCrash("ChooseRoot", ex);
                StatusText.Text = $"백업 폴더 저장 실패: {ex.Message}";
            }
        }
    }

    private static string? Prompt(string message, string title)
    {
        var dialog = new TextPromptWindow(message, title) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private sealed record DeviceChoice(Guid Id, string Label);
    public sealed record ActivityRow(DateTime Time, string Target, string Status, string Message);
}
