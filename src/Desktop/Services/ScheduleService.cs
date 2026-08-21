using System.Text.Json;
using System.IO;

namespace PhoneBackup.Desktop.Services;

public sealed record BackupSchedule(Guid Id, Guid? DeviceId, string Category, bool Enabled, int[] Weekdays, string[] Times, DateTimeOffset? LastOccurrence);

public sealed class ScheduleService
{
    private readonly DatabaseService _database;
    private CancellationTokenSource? _stop;
    private Task? _runner;
    public ScheduleService(DatabaseService database) => _database = database;

    public Task SaveAsync(BackupSchedule schedule) => _database.ExecuteAsync("""
        INSERT INTO schedules(id,device_id,category,enabled,weekdays_json,times_json,last_occurrence)
        VALUES($id,$device,$category,$enabled,$weekdays,$times,$last)
        ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,category=excluded.category,enabled=excluded.enabled,
          weekdays_json=excluded.weekdays_json,times_json=excluded.times_json,last_occurrence=excluded.last_occurrence
        """, p => { p.AddWithValue("$id", schedule.Id.ToString()); p.AddWithValue("$device", (object?)schedule.DeviceId?.ToString() ?? DBNull.Value); p.AddWithValue("$category", schedule.Category); p.AddWithValue("$enabled", schedule.Enabled ? 1 : 0); p.AddWithValue("$weekdays", JsonSerializer.Serialize(schedule.Weekdays)); p.AddWithValue("$times", JsonSerializer.Serialize(schedule.Times)); p.AddWithValue("$last", (object?)schedule.LastOccurrence?.ToString("O") ?? DBNull.Value); });

    public async Task<IReadOnlyList<BackupSchedule>> ListAsync()
    {
        var rows = await _database.QueryAsync("SELECT id,device_id,category,enabled,weekdays_json,times_json,last_occurrence FROM schedules", r => new BackupSchedule(Guid.Parse(r.GetString(0)), r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)), r.GetString(2), r.GetInt32(3) == 1, JsonSerializer.Deserialize<int[]>(r.GetString(4)) ?? [], JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [], r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6))));
        return rows;
    }

    public void Start()
    {
        if (_runner is not null) return;
        _stop = new CancellationTokenSource();
        _runner = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var day = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
                var minute = now.ToString("HH:mm");
                foreach (var schedule in await ListAsync())
                {
                    if (!schedule.Enabled || schedule.DeviceId is null || !schedule.Weekdays.Contains(day) || !schedule.Times.Contains(minute, StringComparer.Ordinal)) continue;
                    var occurrence = now.Date;
                    if (schedule.LastOccurrence?.ToLocalTime().Date == occurrence && schedule.LastOccurrence.Value.ToLocalTime().ToString("HH:mm") == minute) continue;
                    await _database.ExecuteAsync("INSERT INTO backup_requests(id,device_id,status,created_at) VALUES($id,$device,0,$at)", p =>
                    {
                        p.AddWithValue("$id", Guid.NewGuid().ToString()); p.AddWithValue("$device", schedule.DeviceId.Value.ToString()); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                    });
                    await SaveAsync(schedule with { LastOccurrence = now });
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PhoneBackup-scheduler.log"), $"{DateTimeOffset.Now:O} {ex}{Environment.NewLine}"); } catch { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken); } catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        try { _runner?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stop?.Dispose();
        _stop = null;
        _runner = null;
    }
}
