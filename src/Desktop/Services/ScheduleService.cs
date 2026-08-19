using System.Text.Json;

namespace PhoneBackup.Desktop.Services;

public sealed record BackupSchedule(Guid Id, Guid? DeviceId, string Category, bool Enabled, int[] Weekdays, string[] Times, DateTimeOffset? LastOccurrence);

public sealed class ScheduleService
{
    private readonly DatabaseService _database;
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
}
