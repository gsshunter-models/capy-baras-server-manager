using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace CapyServer.Core;

// Carries only the game's UDP traffic; no VPN driver or system route is installed.
public sealed class PlayerBridge : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private UdpClient? udp;
    private TcpClient? tcp;
    private RelayWire? wire;
    private IPEndPoint? game;
    private Task? localLoop, remoteLoop, heartbeat;
    public int LocalPort => udp?.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;
    public bool Connected { get; private set; }
    public event Action<string>? Status;
    public async Task ConnectAsync(RoomInvite invite, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        tcp = new TcpClient { NoDelay = true }; udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            await tcp.ConnectAsync(invite.Control.Host, invite.Control.Port, timeout.Token);
            var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(invite.Fingerprint)));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = invite.Control.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            await ssl.WriteAsync(new byte[] { 2 }.Concat(Convert.FromHexString(invite.PlayerKey)).ToArray(), timeout.Token);
            var ack = new byte[1]; await ssl.ReadExactlyAsync(ack, timeout.Token);
            if (ack[0] != 1) throw new IOException("The room host is offline or this session is full.");
            wire = new RelayWire(ssl); Connected = true; localLoop = LocalAsync(stop.Token); remoteLoop = RemoteAsync(ssl, stop.Token); heartbeat = HeartbeatAsync(stop.Token);
            Status?.Invoke("Joined the friends room through the manager's built-in network.");
        }
        catch { tcp.Dispose(); udp.Dispose(); udp = null; throw; }
    }
    private async Task LocalAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { var packet = await udp!.ReceiveAsync(ct); if (packet.Buffer.Length == 0 || !IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue; game = packet.RemoteEndPoint; await wire!.WriteAsync(1, packet.Buffer, ct); } }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }
    private async Task RemoteAsync(SslStream ssl, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await wire!.ReadAsync(ct); if (frame.Peer == 0) continue;
                if (frame.Peer != 1) throw new InvalidDataException("Invalid room frame.");
                if (game is { } endpoint) await udp!.SendAsync(frame.Bytes, endpoint, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException) { if (!ct.IsCancellationRequested) Status?.Invoke("Room connection closed. Join the session again to reconnect."); }
        finally { Connected = false; ssl.Dispose(); tcp?.Dispose(); udp?.Dispose(); }
    }
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15)); while (await timer.WaitForNextTickAsync(ct)) await wire!.WriteAsync(0, ReadOnlyMemory<byte>.Empty, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); tcp?.Dispose(); udp?.Dispose();
        foreach (var task in new[] { localLoop, remoteLoop, heartbeat }.OfType<Task>()) try { await task; } catch (OperationCanceledException) { }
        Connected = false;
    }
}
