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
    private readonly ContactService _contacts;
    private readonly PairingService _pairing;
    private readonly string _certificatePath;
    private WebApplication? _app;
    private X509Certificate2? _certificate;
    public int Port { get; private set; }
    public string ServerUrl { get; private set; } = "https://127.0.0.1:42817";

    public LocalServer(DatabaseService database, BackupService backups, ContactService contacts, PairingService pairing, string dataRoot)
    { _database = database; _backups = backups; _contacts = contacts; _pairing = pairing; _certificatePath = Path.Combine(dataRoot, "server-certificate.pfx"); }

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
            var temp = Path.Combine(Path.GetTempPath(), $"phonebackup-{Guid.NewGuid():N}.partial");
            try
            {
                await using (var output = File.Create(temp)) await context.Request.Body.CopyToAsync(output);
                var actual = await BackupService.ComputeSha256Async(temp);
                if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "hash_mismatch" });
                await using var input = File.OpenRead(temp);
                var item = new BackupManifestItem(relative, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(relative) : name,
                    new FileInfo(temp).Length, DateTimeOffset.UtcNow, sha, string.IsNullOrWhiteSpace(category) ? "file" : category, null, null, null, null);
                var result = await _backups.StoreAsync(deviceId.Value, runId, item, input);
                return Results.Ok(new { result.Sha256, result.PresentationPath });
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });
        _app.MapPost("/api/v1/sync/finish", async (HttpContext context) =>
        {
            if (await AuthenticateAsync(context) is null) return Results.Unauthorized();
            var payload = await context.Request.ReadFromJsonAsync<FinishPayload>();
            if (payload is null) return Results.BadRequest();
            await _backups.FinishSyncAsync(payload.SyncRunId, payload.Status, payload.FilesSeen, payload.FilesStored, payload.Error);
            return Results.Ok();
        });
        _app.MapPost("/api/v1/sync/progress", async (HttpContext context) =>
        {
            if (await AuthenticateAsync(context) is null) return Results.Unauthorized();
            var payload = await context.Request.ReadFromJsonAsync<ProgressPayload>();
            if (payload is null) return Results.BadRequest();
            await _backups.UpdateProgressAsync(payload.SyncRunId, payload.FilesProcessed, payload.FilesTotal, payload.CurrentFile, payload.Stage);
            return Results.Ok();
        });
        _app.MapGet("/api/v1/contacts", async (HttpContext context) =>
        {
            if (await AuthenticateAsync(context) is null) return Results.Unauthorized();
            return Results.Ok(await _contacts.ListAsync());
        });
        _app.MapPost("/api/v1/contacts/proposals", async (HttpContext context) =>
        {
            if (await AuthenticateAsync(context) is null) return Results.Unauthorized();
            var snapshot = await context.Request.ReadFromJsonAsync<ContactSnapshot>();
            if (snapshot is null) return Results.BadRequest();
            await _contacts.AddProposalAsync(snapshot);
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
}
