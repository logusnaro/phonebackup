using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

public sealed class LocalServer : IDisposable
{
    private readonly DatabaseService _database;
    private readonly BackupService _backups;
    private readonly SmartSwitchService _smartSwitch;
    private readonly PairingService _pairing;
    private readonly string _certificatePath;
    private WebApplication? _app;
    private X509Certificate2? _certificate;
    public int Port { get; private set; }
    public string ServerUrl { get; private set; } = "https://127.0.0.1:42817";

    public LocalServer(DatabaseService database, BackupService backups, SmartSwitchService smartSwitch, PairingService pairing, string dataRoot)
    { _database = database; _backups = backups; _smartSwitch = smartSwitch; _pairing = pairing; _certificatePath = Path.Combine(dataRoot, "server-certificate.pfx"); }

    public async Task StartAsync()
    {
        _certificate = LoadOrCreateCertificate(GetLanAddress());
        _pairing.SetCertificateFingerprint(Convert.ToHexString(SHA256.HashData(_certificate.RawData)));
        Port = 42817;
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Upload bodies are streamed directly to a temporary file and then
            // hash-verified, so Kestrel's 30 MB default request limit is not useful here.
            options.Limits.MaxRequestBodySize = null;
            options.ListenAnyIP(Port, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = _certificate!;
                https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
            }));
        });
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
        _app = builder.Build();
        _app.MapGet("/api/v1/health", () => Results.Ok(new { apiVersion = "1", machine = Environment.MachineName }));
        _app.MapGet("/api/v1/device/ping", async (HttpContext context) =>
            await AuthenticateAsync(context) is null ? Results.Unauthorized() : Results.Ok(new { connected = true, serverTime = DateTimeOffset.UtcNow }));
        _app.MapGet("/api/v1/backup/requests", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var rows = await _database.QueryAsync("""
                SELECT id FROM backup_requests
                WHERE device_id=$device AND status=0
                ORDER BY created_at LIMIT 3
                """, r => r.GetString(0), p => p.AddWithValue("$device", deviceId.Value.ToString()));
            foreach (var requestId in rows)
                await _database.ExecuteAsync("UPDATE backup_requests SET status=1,started_at=$at WHERE id=$id AND device_id=$device AND status IN (0,1)", p =>
                {
                    p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$id", requestId); p.AddWithValue("$device", deviceId.Value.ToString());
                });
            return Results.Ok(rows.Select(id => new { requestId = id }));
        });
        _app.MapPost("/api/v1/backup/requests/{requestId}/result", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var requestId = context.Request.RouteValues["requestId"]?.ToString();
            if (!Guid.TryParse(requestId, out _)) return Results.BadRequest();
            var payload = await context.Request.ReadFromJsonAsync<BackupRequestResultPayload>();
            if (payload is null || !string.Equals(payload.RequestId, requestId, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
            var status = string.Equals(payload.Status, "Completed", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            await _database.ExecuteAsync("UPDATE backup_requests SET status=$status,completed_at=$at,error=$error WHERE id=$id AND device_id=$device AND status=1", p =>
            {
                p.AddWithValue("$status", status); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$error", (object?)payload.Error ?? DBNull.Value);
                p.AddWithValue("$id", requestId); p.AddWithValue("$device", deviceId.Value.ToString());
            });
            return Results.Ok();
        });
        _app.MapPost("/api/v1/smartswitch/request", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var requestId = Guid.NewGuid();
            var launched = _smartSwitch.TryOpenApplication(out var message);
            await _database.ExecuteAsync("""
                INSERT INTO smart_switch_requests(id,device_id,status,created_at,started_at,error)
                VALUES($id,$device,$status,$at,$started,$error)
                """, p =>
            {
                p.AddWithValue("$id", requestId.ToString());
                p.AddWithValue("$device", deviceId.Value.ToString());
                p.AddWithValue("$status", launched ? 1 : 3);
                p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                p.AddWithValue("$started", launched ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
                p.AddWithValue("$error", launched ? DBNull.Value : message);
            });
            return Results.Ok(new { requestId, launched, message });
        });
        _app.MapPost("/api/v1/pair/claim", async (HttpContext context) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<PairRequest>();
                if (request is null) return Results.BadRequest(new { error = "pair_request_invalid" });
                var response = await _pairing.ClaimAsync(request, GetServerUrl());
                return response is null ? Results.BadRequest(new { error = "pairing_ticket_invalid_or_expired" }) : Results.Ok(response);
            }
            catch (Exception ex)
            {
                var log = Path.Combine(Path.GetTempPath(), "PhoneBackup-pairing-error.log");
                await File.AppendAllTextAsync(log, $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
                return Results.Problem("PC 페어링 처리 중 오류가 발생했습니다.", statusCode: 500);
            }
        });
        _app.MapPost("/api/v1/sync/start", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var runId = await _backups.StartSyncAsync(deviceId.Value);
            return Results.Ok(new { syncRunId = runId });
        });
        _app.MapPut("/api/v1/sync/file", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            if (!Guid.TryParse(context.Request.Query["syncRunId"], out var runId)) return Results.BadRequest();
            var relative = DecodeUtf8Header(context, "X-Relative-Path-B64", "X-Relative-Path");
            var sha = context.Request.Headers["X-SHA256"].ToString();
            var name = DecodeUtf8Header(context, "X-Original-File-Name-B64", "X-Original-File-Name");
            var category = context.Request.Headers["X-Category"].ToString();
            if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(sha)) return Results.BadRequest(new { error = "manifest_headers_missing" });
            if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) return Results.BadRequest(new { error = "sha256_invalid" });
            var offsetHeader = context.Request.Headers["X-Chunk-Offset"].ToString();
            var totalHeader = context.Request.Headers["X-Chunk-Total"].ToString();
            long chunkOffset = 0, chunkTotal = 0;
            var chunked = long.TryParse(offsetHeader, out chunkOffset) && long.TryParse(totalHeader, out chunkTotal) && chunkOffset >= 0 && chunkTotal >= 0;
            var tempKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{runId:N}|{relative}"))).ToLowerInvariant();
            var temp = Path.Combine(Path.GetTempPath(), $"phonebackup-{tempKey}.partial");
            // Preserve an incomplete chunk file when the connection drops while
            // copying the request body. It is deleted once a complete file is
            // available (or if validation fails).
            var keepPartial = chunked;
            try
            {
                if (chunked)
                {
                    var current = File.Exists(temp) ? new FileInfo(temp).Length : 0L;
                    if (current != chunkOffset) { keepPartial = true; return Results.Conflict(new { nextOffset = current }); }
                    await using var output = new FileStream(temp, FileMode.Append, FileAccess.Write, FileShare.Read);
                    await context.Request.Body.CopyToAsync(output);
                    var received = new FileInfo(temp).Length;
                    if (received < chunkTotal) { keepPartial = true; return Results.Ok(new { complete = false, nextOffset = received }); }
                    if (received > chunkTotal) { keepPartial = false; return Results.BadRequest(new { error = "chunk_total_mismatch" }); }
                    keepPartial = false;
                }
                else
                {
                    await using var output = File.Create(temp);
                    await context.Request.Body.CopyToAsync(output);
                }
                var actual = await BackupService.ComputeSha256Async(temp);
                if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "hash_mismatch" });
                await using var input = File.OpenRead(temp);
                var item = string.Equals(category, "recording", StringComparison.OrdinalIgnoreCase)
                    ? BackupService.ParseRecording(relative, new FileInfo(temp).Length, DateTimeOffset.UtcNow, sha)
                    : new BackupManifestItem(relative, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(relative) : name,
                        new FileInfo(temp).Length, DateTimeOffset.UtcNow, sha, string.IsNullOrWhiteSpace(category) ? "file" : category, null, null, null, null);
                var result = await _backups.StoreAsync(deviceId.Value, runId, item, input);
                return Results.Ok(new { result.Sha256, result.PresentationPath });
            }
            finally { if (!keepPartial && File.Exists(temp)) File.Delete(temp); }
        });
        _app.MapGet("/api/v1/sync/known", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var relative = DecodeUtf8Header(context, "X-Relative-Path-B64", "X-Relative-Path");
            var sha = context.Request.Query["sha256"].ToString();
            if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(sha)) return Results.BadRequest();
            return Results.Ok(new { known = await _backups.IsKnownAsync(deviceId.Value, relative, sha) });
        });
        _app.MapPost("/api/v1/sync/finish", async (HttpContext context) =>
        {
            if (await AuthenticateAsync(context) is null) return Results.Unauthorized();
            var payload = await context.Request.ReadFromJsonAsync<FinishPayload>();
            if (payload is null) return Results.BadRequest();
            await _backups.FinishSyncAsync(payload.SyncRunId, payload.Status, payload.FilesSeen, payload.FilesStored, payload.Error);
            return Results.Ok();
        });
        _app.MapGet("/api/v1/deletions/candidates", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var days = int.TryParse(context.Request.Query["olderThanDays"], out var requested) ? Math.Clamp(requested, 1, 3650) : 90;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
            var rows = await _database.QueryAsync("""
                SELECT b.id,b.relative_path,b.sha256,b.verified_at
                FROM backup_items b
                WHERE b.device_id=$device AND b.category='recording' AND b.verified_at IS NOT NULL
                  AND COALESCE(b.recorded_at,b.last_modified_at) <= $cutoff
                  AND EXISTS (
                    SELECT 1 FROM smart_switch_backups s
                    WHERE s.device_id=b.device_id AND s.status=1 AND s.verified_at IS NOT NULL
                      AND s.completed_at >= COALESCE(b.recorded_at,b.last_modified_at)
                  )
                  AND NOT EXISTS (SELECT 1 FROM deletion_candidates c WHERE c.id=b.id AND c.approved_at IS NOT NULL)
                ORDER BY COALESCE(b.recorded_at,b.last_modified_at)
                """, r => new { Id = Guid.Parse(r.GetString(0)), RelativePath = r.GetString(1), Sha256 = r.GetString(2), VerifiedAt = r.GetString(3) }, p => { p.AddWithValue("$device", deviceId.Value.ToString()); p.AddWithValue("$cutoff", cutoff); });
            foreach (var row in rows)
            {
                await _database.ExecuteAsync("""
                    INSERT INTO deletion_candidates(id,device_id,relative_path,sha256,verified_at,eligible_at,approved_at)
                    VALUES($id,$device,$path,$sha,$verified,$eligible,NULL)
                    ON CONFLICT(id) DO UPDATE SET sha256=excluded.sha256,verified_at=excluded.verified_at,eligible_at=excluded.eligible_at
                    """, p => { p.AddWithValue("$id", row.Id.ToString()); p.AddWithValue("$device", deviceId.Value.ToString()); p.AddWithValue("$path", row.RelativePath); p.AddWithValue("$sha", row.Sha256); p.AddWithValue("$verified", row.VerifiedAt); p.AddWithValue("$eligible", cutoff); });
            }
            return Results.Ok(rows.Select(x => new { id = x.Id, relativePath = x.RelativePath, sha256 = x.Sha256 }));
        });
        _app.MapGet("/api/v1/deletions/requests", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
            var rows = await _database.QueryAsync("""
                SELECT id,items_json FROM deletion_requests
                WHERE device_id=$device AND (status=0 OR (status=1 AND created_at <= $cutoff))
                ORDER BY created_at LIMIT 5
                """, r => new { Id = r.GetString(0), Items = System.Text.Json.JsonDocument.Parse(r.GetString(1)).RootElement.Clone() }, p =>
            {
                p.AddWithValue("$device", deviceId.Value.ToString()); p.AddWithValue("$cutoff", cutoff);
            });
            foreach (var row in rows)
                await _database.ExecuteAsync("UPDATE deletion_requests SET status=1 WHERE id=$id AND device_id=$device", p => { p.AddWithValue("$id", row.Id); p.AddWithValue("$device", deviceId.Value.ToString()); });
            return Results.Ok(rows.Select(x => new { requestId = x.Id, items = x.Items }));
        });
        _app.MapPost("/api/v1/deletions/requests/{requestId}/results", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var requestId = context.Request.RouteValues["requestId"]?.ToString();
            if (!Guid.TryParse(requestId, out _)) return Results.BadRequest();
            var results = await context.Request.ReadFromJsonAsync<List<DeletionResultPayload>>();
            if (results is null) return Results.BadRequest();
            var request = await _database.QueryAsync("SELECT 1 FROM deletion_requests WHERE id=$id AND device_id=$device AND status=1 LIMIT 1", _ => true,
                p => { p.AddWithValue("$id", requestId); p.AddWithValue("$device", deviceId.Value.ToString()); });
            if (request.Count == 0) return Results.NotFound();
            await _database.ExecuteAsync("UPDATE deletion_requests SET status=2,completed_at=$at,result_json=$results WHERE id=$id AND device_id=$device", p =>
            {
                p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$results", System.Text.Json.JsonSerializer.Serialize(results));
                p.AddWithValue("$id", requestId); p.AddWithValue("$device", deviceId.Value.ToString());
            });
            foreach (var result in results.Where(x => x.Deleted))
            {
                await _database.ExecuteAsync("UPDATE deletion_candidates SET approved_at=$at WHERE id=$id AND device_id=$device AND relative_path=$path", p =>
                {
                    p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$id", result.Id.ToString()); p.AddWithValue("$device", deviceId.Value.ToString()); p.AddWithValue("$path", result.RelativePath);
                });
                await _database.ExecuteAsync("INSERT INTO audit_log(event_type,subject_id,details_json,created_at) VALUES('file_deleted_on_device',$id,$details,$at)", p =>
                {
                    p.AddWithValue("$id", result.Id.ToString()); p.AddWithValue("$details", System.Text.Json.JsonSerializer.Serialize(new { result.RelativePath, result.Reason, requestId })); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                });
            }
            return Results.Ok(new { received = results.Count, deleted = results.Count(x => x.Deleted) });
        });
        _app.MapPost("/api/v1/deletions/results", async (HttpContext context) =>
        {
            var deviceId = await AuthenticateAsync(context); if (deviceId is null) return Results.Unauthorized();
            var results = await context.Request.ReadFromJsonAsync<List<DeletionResultPayload>>();
            if (results is null) return Results.BadRequest();
            foreach (var result in results.Where(x => x.Deleted))
            {
                await _database.ExecuteAsync("""
                    UPDATE deletion_candidates SET approved_at=$at
                    WHERE id=$id AND device_id=$device AND relative_path=$path
                    """, p => { p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$id", result.Id.ToString()); p.AddWithValue("$device", deviceId.Value.ToString()); p.AddWithValue("$path", result.RelativePath); });
                await _database.ExecuteAsync("INSERT INTO audit_log(event_type,subject_id,details_json,created_at) VALUES($event,$subject,$details,$at)", p => { p.AddWithValue("$event", "file_deleted_on_device"); p.AddWithValue("$subject", result.Id.ToString()); p.AddWithValue("$details", System.Text.Json.JsonSerializer.Serialize(new { result.RelativePath, result.Reason })); p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); });
            }
            return Results.Ok(new { received = results.Count, deleted = results.Count(x => x.Deleted) });
        });
        _app.MapPost("/api/v1/sync/progress", async (HttpContext context) =>
        {
            if (await AuthenticateAsync(context) is null) return Results.Unauthorized();
            var payload = await context.Request.ReadFromJsonAsync<ProgressPayload>();
            if (payload is null) return Results.BadRequest();
            await _backups.UpdateProgressAsync(payload.SyncRunId, payload.FilesProcessed, payload.FilesTotal, payload.CurrentFile, payload.Stage);
            return Results.Ok();
        });
        await _app.StartAsync();
        ServerUrl = GetServerUrl();
    }

    private async Task<Guid?> AuthenticateAsync(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.Headers["X-Device-Id"].ToString(), out var deviceId)) return null;
        var token = context.Request.Headers["X-Device-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token) || !await _pairing.IsValidDeviceAsync(deviceId, token)) return null;
        await _database.ExecuteAsync("UPDATE devices SET last_seen_at=$at,status=1 WHERE id=$id", p => { p.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); p.AddWithValue("$id", deviceId.ToString()); });
        return deviceId;
    }

    private string GetServerUrl() => $"https://{GetLanAddress()}:{Port}";
    private static string DecodeUtf8Header(HttpContext context, string encodedName, string legacyName)
    {
        var encoded = context.Request.Headers[encodedName].ToString();
        if (!string.IsNullOrWhiteSpace(encoded))
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); } catch (FormatException) { }
        return context.Request.Headers[legacyName].ToString();
    }
    private static string GetLanAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var address in ni.GetIPProperties().UnicastAddresses.Select(x => x.Address))
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)) return address.ToString();
        return "127.0.0.1";
    }

    private static X509Certificate2 CreateCertificate(string lanAddress)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PhoneBackup.Local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("PhoneBackup.Local");
        names.AddIpAddress(IPAddress.Parse(lanAddress));
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") }; // Server Authentication
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        // Re-import the generated PFX so Windows Schannel/Kestrel receives a
        // persisted private-key handle. Passing the ephemeral CertificateRequest
        // result directly can make the listener accept TCP but abort every TLS
        // ClientHello with SEC_E_NO_CREDENTIALS.
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    private X509Certificate2 LoadOrCreateCertificate(string lanAddress)
    {
        if (File.Exists(_certificatePath))
            return new X509Certificate2(File.ReadAllBytes(_certificatePath), (string?)null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        var certificate = CreateCertificate(lanAddress);
        Directory.CreateDirectory(Path.GetDirectoryName(_certificatePath)!);
        File.WriteAllBytes(_certificatePath, certificate.Export(X509ContentType.Pfx));
        return certificate;
    }

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _certificate?.Dispose();
    }

    private sealed record FinishPayload(Guid SyncRunId, SyncRunStatus Status, int FilesSeen, int FilesStored, string? Error);
    private sealed record ProgressPayload(Guid SyncRunId, int FilesProcessed, int FilesTotal, string? CurrentFile, string Stage);
    private sealed record BackupRequestResultPayload(string RequestId, string Status, string? Error);
}
