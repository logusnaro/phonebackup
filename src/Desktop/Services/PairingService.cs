using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed class PairingService
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly DatabaseService _database;
    private readonly Dictionary<string, PairingTicket> _tickets = new();
    private readonly object _sync = new();
    private string _certificateSha256;

    public PairingService(DatabaseService database)
    {
        _database = database;
        _certificateSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)));
    }

    public string CertificateSha256 => _certificateSha256;
    public void SetCertificateFingerprint(string fingerprint) => _certificateSha256 = fingerprint;

    public async Task<PairingTicket> CreateTicketAsync(Guid memberId, string serverUrl)
    {
        var ticketId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pairingCode = CreatePairingCode();
        var ticket = new PairingTicket(ticketId, token, serverUrl, CertificateSha256,
            DateTimeOffset.UtcNow.AddMinutes(10), pairingCode);
        lock (_sync) _tickets[ticketId] = ticket;
        await _database.ExecuteAsync(
            "INSERT INTO pairing_tickets(id,member_id,device_token_hash,expires_at) VALUES ($id,$member,$hash,$expires)",
            p => { p.AddWithValue("$id", ticketId); p.AddWithValue("$member", memberId.ToString());
                p.AddWithValue("$hash", Hash(token)); p.AddWithValue("$expires", ticket.ExpiresAt.ToString("O")); });
        return ticket;
    }

    public PairingTicket? FindByCode(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        lock (_sync)
        {
            var ticket = _tickets.Values.FirstOrDefault(x => x.PairingCode == normalized);
            return ticket is not null && ticket.ExpiresAt > DateTimeOffset.UtcNow ? ticket : null;
        }
    }

    private static string CreatePairingCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        return string.Concat(bytes.Select(b => CodeAlphabet[b % CodeAlphabet.Length]));
    }

    public async Task<PairResponse?> ClaimAsync(PairRequest request, string serverUrl)
    {
        PairingTicket? ticket;
        lock (_sync) _tickets.TryGetValue(request.TicketId, out ticket);
        if (ticket is null || ticket.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var memberRows = await _database.QueryAsync("SELECT member_id FROM pairing_tickets WHERE id=$id AND used_at IS NULL AND expires_at > $now",
            r => r.GetString(0), p => { p.AddWithValue("$id", request.TicketId); p.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); });
        if (memberRows.Count == 0) return null;
        var memberId = Guid.Parse(memberRows[0]);
        var deviceId = Guid.NewGuid();
        var tokenHash = Hash(ticket.DeviceToken);
        await _database.ExecuteAsync("""
            INSERT INTO devices(id,member_id,display_name,platform,model,android_version,status,last_seen_at,token_hash)
            VALUES($id,$member,$name,$platform,$model,$android,$status,$seen,$hash);
            UPDATE pairing_tickets SET used_at=$used WHERE id=$ticket;
            INSERT INTO audit_log(event_type,subject_id,details_json,created_at) VALUES('device_paired',$id,$details,$at);
            """, p => { p.AddWithValue("$id", deviceId.ToString()); p.AddWithValue("$member", memberId.ToString());
                p.AddWithValue("$name", request.DeviceName); p.AddWithValue("$platform", "Android");
                p.AddWithValue("$model", request.Model); p.AddWithValue("$android", request.AndroidVersion);
                p.AddWithValue("$status", (int)DeviceStatus.Active); p.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
                p.AddWithValue("$hash", tokenHash); p.AddWithValue("$ticket", request.TicketId);
                p.AddWithValue("$used", DateTimeOffset.UtcNow.ToString("O"));
                p.AddWithValue("$details", JsonSerializer.Serialize(request)); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); });
        lock (_sync) _tickets.Remove(request.TicketId);
        return new PairResponse(deviceId, memberId, ticket.DeviceToken, serverUrl, ticket.CertificateSha256, "1");
    }

    public async Task<bool> IsValidDeviceAsync(Guid deviceId, string token)
    {
        var rows = await _database.QueryAsync("SELECT token_hash FROM devices WHERE id=$id AND status=$status",
            r => r.GetString(0), p => { p.AddWithValue("$id", deviceId.ToString()); p.AddWithValue("$status", (int)DeviceStatus.Active); });
        return rows.Count == 1 && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(rows[0]), Convert.FromHexString(Hash(token)));
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
