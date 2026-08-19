using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop.Services;

/// <summary>
/// Keeps the phone pairing flow to a short code. The code is only useful on
/// the local Wi-Fi and maps to a single-use HTTPS ticket that still performs
/// the normal certificate-pinned claim.
/// </summary>
public sealed class PairingDiscoveryService : IDisposable
{
    public const int Port = 42818;
    private readonly PairingService _pairing;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    public PairingDiscoveryService(PairingService pairing) => _pairing = pairing;

    public void Start() => _listener ??= Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        try
        {
            using var udp = new UdpClient(Port) { EnableBroadcast = true };
            while (!_stop.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(_stop.Token);
                var request = Encoding.UTF8.GetString(received.Buffer).Split('|', StringSplitOptions.TrimEntries);
                if (request.Length != 3 || request[0] != "PHONEBACKUP_PAIR_V1") continue;
                var ticket = _pairing.FindByCode(request[1]);
                if (ticket is null) continue;
                var response = new PairingDiscoveryResponse(request[2], ticket.TicketId, ticket.ServerUrl, ticket.CertificateSha256);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
                await udp.SendAsync(bytes, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
    }
}
