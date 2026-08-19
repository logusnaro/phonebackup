using System.Text.Json;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed class ContactService
{
    private readonly DatabaseService _database;
    public ContactService(DatabaseService database) => _database = database;

    public async Task<IReadOnlyList<Contact>> ListAsync()
    {
        var rows = await _database.QueryAsync("""
            SELECT id,display_name,company,notes,updated_at FROM contacts WHERE deleted_at IS NULL ORDER BY display_name
            """, r => new { Id = Guid.Parse(r.GetString(0)), Name = r.GetString(1), Company = r.IsDBNull(2) ? null : r.GetString(2), Notes = r.IsDBNull(3) ? null : r.GetString(3), Updated = DateTimeOffset.Parse(r.GetString(4)) });
        var contacts = new List<Contact>();
        foreach (var row in rows)
        {
            var phones = await _database.QueryAsync("SELECT value FROM contact_phones WHERE contact_id=$id", x => x.GetString(0), p => p.AddWithValue("$id", row.Id.ToString()));
            var emails = await _database.QueryAsync("SELECT value FROM contact_emails WHERE contact_id=$id", x => x.GetString(0), p => p.AddWithValue("$id", row.Id.ToString()));
            contacts.Add(new Contact(row.Id, row.Name, phones, emails, row.Company, row.Notes, row.Updated));
        }
        return contacts;
    }

    public async Task SaveAsync(Contact contact)
    {
        await _database.ExecuteAsync("""
            INSERT INTO contacts(id,display_name,company,notes,updated_at,deleted_at) VALUES($id,$name,$company,$notes,$at,NULL)
            ON CONFLICT(id) DO UPDATE SET display_name=excluded.display_name,company=excluded.company,notes=excluded.notes,updated_at=excluded.updated_at,deleted_at=NULL;
            DELETE FROM contact_phones WHERE contact_id=$id; DELETE FROM contact_emails WHERE contact_id=$id;
            """, p => { p.AddWithValue("$id", contact.Id.ToString()); p.AddWithValue("$name", contact.DisplayName); p.AddWithValue("$company", (object?)contact.Company ?? DBNull.Value); p.AddWithValue("$notes", (object?)contact.Notes ?? DBNull.Value); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); });
        foreach (var phone in contact.PhoneNumbers.Distinct()) await _database.ExecuteAsync("INSERT INTO contact_phones(contact_id,value,normalized) VALUES($id,$value,$normalized)", p => { p.AddWithValue("$id", contact.Id.ToString()); p.AddWithValue("$value", phone); p.AddWithValue("$normalized", PhoneNumberNormalizer.Normalize(phone)); });
        foreach (var email in contact.Emails.Distinct(StringComparer.OrdinalIgnoreCase)) await _database.ExecuteAsync("INSERT INTO contact_emails(contact_id,value,normalized) VALUES($id,$value,$normalized)", p => { p.AddWithValue("$id", contact.Id.ToString()); p.AddWithValue("$value", email); p.AddWithValue("$normalized", email.Trim().ToLowerInvariant()); });
    }

    public Task DeleteAsync(Guid contactId) => _database.ExecuteAsync("UPDATE contacts SET deleted_at=$at,updated_at=$at WHERE id=$id", p => { p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$id", contactId.ToString()); });

    public Task AddProposalAsync(ContactSnapshot snapshot) => _database.ExecuteAsync("INSERT INTO contact_proposals(id,device_id,payload_json,created_at) VALUES($id,$device,$json,$at)", p => { p.AddWithValue("$id", Guid.NewGuid().ToString()); p.AddWithValue("$device", snapshot.DeviceId.ToString()); p.AddWithValue("$json", JsonSerializer.Serialize(snapshot)); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); });
}
