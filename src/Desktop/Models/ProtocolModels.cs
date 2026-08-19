namespace PhoneBackup.Desktop.Models;

public sealed record PairRequest(string TicketId, string DeviceName, string Model, string AndroidVersion,
    string DevicePublicKey);
public sealed record PairResponse(Guid DeviceId, Guid MemberId, string DeviceToken,
    string ServerUrl, string CertificateSha256, string ApiVersion);
public sealed record PairingDiscoveryResponse(string Nonce, string TicketId, string ServerUrl,
    string CertificateSha256);
public sealed record ManifestRequest(Guid SyncRunId, Guid DeviceId, IReadOnlyList<BackupManifestItem> Items);
public sealed record ChunkRequest(Guid SyncRunId, string RelativePath, string Sha256, long Offset, long TotalBytes);
public sealed record ContactSnapshot(Guid DeviceId, IReadOnlyList<Contact> Contacts);
public sealed record DeleteRequest(Guid DeviceId, IReadOnlyList<DeletionCandidate> Items);
public sealed record RestoreRequest(Guid DeviceId, Guid StoredObjectId, string DestinationRelativePath,
    string ConflictMode);
