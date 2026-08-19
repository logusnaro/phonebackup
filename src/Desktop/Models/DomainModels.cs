namespace PhoneBackup.Desktop.Models;

public enum MemberStatus { Active, Archived }
public enum DeviceStatus { Pending, Active, Retired, Revoked }
public enum SyncRunStatus { Running, Completed, Partial, Failed, Skipped }
public enum ContactProposalStatus { Pending, Approved, Rejected }

public sealed record Member(Guid Id, string Name, MemberStatus Status, DateTimeOffset CreatedAt);
public sealed record Device(Guid Id, Guid MemberId, string DisplayName, string Platform, string? Model,
    string? AndroidVersion, DeviceStatus Status, DateTimeOffset? LastSeenAt, string TokenHash);
public sealed record BackupManifestItem(string RelativePath, string OriginalFileName, long SizeBytes,
    DateTimeOffset LastModifiedAt, string Sha256, string Category, int? DurationSeconds,
    DateTimeOffset? RecordedAt, string? ParsedPhoneNumber, string? ParsedContactName);
public sealed record SyncRun(Guid Id, Guid DeviceId, SyncRunStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, int FilesSeen, int FilesStored, string? Error);
public sealed record Contact(Guid Id, string DisplayName, IReadOnlyList<string> PhoneNumbers,
    IReadOnlyList<string> Emails, string? Company, string? Notes, DateTimeOffset UpdatedAt);
public sealed record ContactConflict(Guid Id, Guid? ContactAId, Guid? ContactBId, string Reason,
    ContactProposalStatus Status, DateTimeOffset CreatedAt);
public sealed record PairingTicket(string TicketId, string DeviceToken, string ServerUrl,
    string CertificateSha256, DateTimeOffset ExpiresAt, string PairingCode);
public sealed record DeletionCandidate(Guid Id, Guid DeviceId, string RelativePath, string Sha256,
    DateTimeOffset VerifiedAt, DateTimeOffset EligibleAt);

public static class PhoneNumberNormalizer
{
    public static string Normalize(string input, string defaultCountryCode = "82")
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("001", StringComparison.Ordinal)) digits = digits[3..];
        else if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.StartsWith("0", StringComparison.Ordinal)) digits = defaultCountryCode + digits[1..];
        return digits.Length == 0 ? string.Empty : "+" + digits;
    }
}
