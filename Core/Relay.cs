using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CapyServer.Core;

public sealed record JoinAddress(string Host, int Port)
{
    public const int DefaultPort = 19800;
    public string GameAddress => Port == DefaultPort ? Host : ToString();
    public override string ToString() => Host + ":" + Port;
    public static JoinAddress Parse(string text)
    {
        text = text.Trim(); if (!text.Contains("://")) text = "mxsimulator://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "mxsimulator" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || (uri.AbsolutePath is not ("" or "/")) || Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown || uri.Host.Contains(':') || uri.Port is 0 or > 65535) throw new InvalidDataException("Enter a hostname, host:port address or mxsimulator:// link.");
        return new(uri.IdnHost, uri.Port == -1 ? DefaultPort : uri.Port);
    }
    public string Link => "mxsimulator://" + this;
}
public sealed record RelayPairing(JoinAddress Control, int GamePort, string Key, string Fingerprint)
{
    public string Encode() => $"capyrelay://{Control}?game={GamePort}&key={Key}&cert={Fingerprint}";
    public static RelayPairing Parse(string code)
    {
        if (code.Length > 2000 || !Uri.TryCreate(code.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "capyrelay" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath is not ("" or "/")) throw new InvalidDataException("Invalid private relay pairing code.");
        var address = JoinAddress.Parse(uri.Host + ":" + uri.Port);
        var pairs = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToArray();
        if (pairs.Any(p => p.Length != 2) || pairs.GroupBy(p => p[0]).Any(g => g.Count() != 1)) throw new InvalidDataException("Invalid relay pairing fields.");
        var fields = pairs.ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (!fields.TryGetValue("game", out var game) || !int.TryParse(game, out var port) || port is < 1024 or > 65535 || !fields.TryGetValue("key", out var key) || key.Length != 64 || !key.All(Uri.IsHexDigit) || !fields.TryGetValue("cert", out var cert) || cert.Length != 64 || !cert.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid relay key, certificate, or game port.");
        return new(address, port, key, cert.ToUpperInvariant());
    }
}
public sealed record RoomInvite(JoinAddress Control, int GamePort, string PlayerKey, string Fingerprint)
{
    public string Encode() => new RelayPairing(Control, GamePort, PlayerKey, Fingerprint).Encode().Replace("capyrelay://", "capyroom://");
    public static RoomInvite Parse(string code)
    {
        if (!code.Trim().StartsWith("capyroom://", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paste a Capy room invite.");
        var p = RelayPairing.Parse("capyrelay://" + code.Trim()[11..]); return new(p.Control, p.GamePort, p.Key, p.Fingerprint);
    }
    public static string DerivePlayerKey(string hostKey) => Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(hostKey), Encoding.UTF8.GetBytes("capy-player-room-v1")));
}
public sealed class RelayIdentity : IDisposable
{
    public string Key { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public string PlayerKey => RoomInvite.DerivePlayerKey(Key);
    private RelayIdentity(string key, X509Certificate2 certificate) { Key = key; Certificate = certificate; }
    public static RelayIdentity Load(string folder)
    {
        Directory.CreateDirectory(folder); var keyPath = Path.Combine(folder, "host-key.txt"); var certPath = Path.Combine(folder, "identity.pfx");
        var key = File.Exists(keyPath) ? File.ReadAllText(keyPath).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("Relay identity key is invalid.");
        var storage = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        if (File.Exists(certPath)) return new(key, X509CertificateLoader.LoadPkcs12FromFile(certPath, key, storage));
        using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=Capy Bara Server Relay", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        var pfx = cert.Export(X509ContentType.Pfx, key); File.WriteAllBytes(certPath, pfx); File.WriteAllText(keyPath, key);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(certPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        return new(key, X509CertificateLoader.LoadPkcs12(pfx, key, storage));
    }
    public void Dispose() => Certificate.Dispose();
}
internal sealed class RelayWire(Stream stream)
{
    private readonly SemaphoreSlim send = new(1);
    public async Task WriteAsync(int peer, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length > 65507 || peer < 0) throw new InvalidDataException("Invalid relay packet.");
        var header = new byte[6]; BinaryPrimitives.WriteInt32BigEndian(header, peer); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)bytes.Length);
        await send.WaitAsync(ct); try { await stream.WriteAsync(header, ct); if (bytes.Length > 0) await stream.WriteAsync(bytes, ct); } finally { send.Release(); }
    }
    public async Task<(int Peer, byte[] Bytes)> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45)); ct = timeout.Token;
        var header = new byte[6]; await stream.ReadExactlyAsync(header, ct); var peer = BinaryPrimitives.ReadInt32BigEndian(header); var size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (size > 65507 || peer < 0 || (peer == 0 && size != 0) || (peer != 0 && size == 0)) throw new InvalidDataException("Invalid relay frame.");
        var bytes = new byte[size]; if (size > 0) await stream.ReadExactlyAsync(bytes, ct); return (peer, bytes);
    }
}
public sealed class PublicRelay : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, Peer> peers = new();
    private readonly ConcurrentDictionary<string, int> ids = new();
    private readonly SemaphoreSlim hostGate = new(1);
    private readonly SemaphoreSlim authGate = new(16);
    private readonly ConcurrentDictionary<int, (TcpClient Client, RelayWire Wire)> players = new();
    private readonly ConcurrentDictionary<TcpClient, byte> connections = new();
    private readonly RelayIdentity identity;
    private UdpClient? udp;
    private TcpListener? listener;
    private RelayWire? wire;
    private TcpClient? activeClient;
    private Task? receiveTask, acceptTask;
    private int nextId;
    public int ConnectedPeers => peers.Count + players.Count;
    public bool HostConnected => wire != null;
    public event Action<string>? Status;
    private sealed record Peer(IPEndPoint Address) { public DateTime Seen = DateTime.UtcNow; }
    public PublicRelay(RelayIdentity identity) { this.identity = identity; }
    public void Start(IPAddress address, int udpPort, int tcpPort)
    {
        if (udpPort is < 1024 or > 65535 || tcpPort is < 1024 or > 65535) throw new InvalidDataException("Relay ports must be 1024–65535.");
        if (udp != null) throw new InvalidOperationException("Relay is already running.");
        udp = new UdpClient(new IPEndPoint(address, udpPort));
        try { listener = new TcpListener(address, tcpPort); listener.Start(8); } catch { udp.Dispose(); udp = null; throw; }
        receiveTask = ReceiveAsync(stop.Token); acceptTask = AcceptAsync(stop.Token); Status?.Invoke("Relay listening. Waiting for the private host pairing connection.");
    }
    private async Task ReceiveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await udp!.ReceiveAsync(ct); var current = wire;
                if (current == null || packet.Buffer.Length == 0 || packet.Buffer.Length > 65507) continue;
                foreach (var old in peers.Where(p => DateTime.UtcNow - p.Value.Seen > TimeSpan.FromMinutes(2)).ToArray()) if (peers.TryRemove(old.Key, out var removed)) ids.TryRemove(removed.Address.ToString(), out _);
                var key = packet.RemoteEndPoint.ToString();
                if (!ids.TryGetValue(key, out var id)) { if (ConnectedPeers >= 160) continue; id = Interlocked.Increment(ref nextId); peers[id] = new Peer(packet.RemoteEndPoint); ids[key] = id; }
                if (peers.TryGetValue(id, out var peer)) peer.Seen = DateTime.UtcNow;
                try { await current.WriteAsync(id, packet.Buffer, ct); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { activeClient?.Dispose(); }
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task AcceptAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { var client = await listener!.AcceptTcpClientAsync(ct); if (!authGate.Wait(0)) { client.Dispose(); continue; } connections[client] = 0; _ = HandleConnectionAsync(client, ct); } }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var host = false; var authenticating = true; var playerId = 0;
        try
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream(), false))
            {
                client.NoDelay = true; client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                using var auth = CancellationTokenSource.CreateLinkedTokenSource(ct); auth.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = identity.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, auth.Token);
                var hello = new byte[33]; await ssl.ReadExactlyAsync(hello, auth.Token); var role = hello[0]; var key = hello.AsSpan(1).ToArray();
                if (role is not (1 or 2) || !CryptographicOperations.FixedTimeEquals(key, Convert.FromHexString(role == 1 ? identity.Key : identity.PlayerKey))) return;
                var connected = new RelayWire(ssl);
                if (role == 1)
                {
                    if (!await hostGate.WaitAsync(0, auth.Token)) return;
                    host = true; activeClient = client; wire = connected; Status?.Invoke("Private session host connected through the in-app network.");
                }
                else
                {
                    if (wire == null || ConnectedPeers >= 160) { await ssl.WriteAsync(new byte[] { 2 }, auth.Token); return; }
                    playerId = Interlocked.Increment(ref nextId); players[playerId] = (client, connected);
                }
                await ssl.WriteAsync(new byte[] { 1 }, auth.Token);
                authGate.Release(); authenticating = false;
                while (!ct.IsCancellationRequested)
                {
                    var frame = await connected.ReadAsync(ct);
                    if (frame.Peer == 0) { await connected.WriteAsync(0, ReadOnlyMemory<byte>.Empty, ct); continue; }
                    if (host)
                    {
                        if (players.TryGetValue(frame.Peer, out var player)) { try { await player.Wire.WriteAsync(1, frame.Bytes, ct); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { players.TryRemove(frame.Peer, out _); player.Client.Dispose(); } }
                        else if (peers.TryGetValue(frame.Peer, out var peer)) { try { await udp!.SendAsync(frame.Bytes, peer.Address, ct); } catch (SocketException) when (!ct.IsCancellationRequested) { } }
                    }
                    else
                    {
                        if (frame.Peer != 1) throw new InvalidDataException("Invalid player frame.");
                        var current = wire ?? throw new IOException("The session host disconnected."); await current.WriteAsync(playerId, frame.Bytes, ct);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { if (host && !ct.IsCancellationRequested) Status?.Invoke("Session host disconnected. Room is waiting for reconnection."); }
        finally
        {
            connections.TryRemove(client, out _); if (authenticating) authGate.Release();
            if (playerId != 0) players.TryRemove(playerId, out _);
            if (host) { wire = null; activeClient = null; peers.Clear(); ids.Clear(); foreach (var player in players.Values) player.Client.Dispose(); players.Clear(); hostGate.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); foreach (var client in connections.Keys) client.Dispose(); activeClient?.Dispose(); udp?.Dispose(); listener?.Stop();
        foreach (var task in new[] { receiveTask, acceptTask }.OfType<Task>()) try { await task; } catch (OperationCanceledException) { }
    }
}
public sealed class HostTunnel : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, (UdpClient Socket, DateTime Seen)> peers = new();
    private readonly int localPort;
    private readonly IPAddress localAddress;
    private TcpClient? tcp;
    private Task? loop, heartbeat;
    private RelayWire? wire;
    public bool Connected { get; private set; }
    public event Action<string>? Status;
    public HostTunnel(int localPort, IPAddress? localAddress = null) { this.localPort = localPort; this.localAddress = localAddress ?? IPAddress.Loopback; }
    public async Task ConnectAsync(RelayPairing pairing, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var client = new TcpClient { NoDelay = true }; tcp = client;
        try
        {
            await client.ConnectAsync(pairing.Control.Host, pairing.Control.Port, timeout.Token);
            var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pairing.Fingerprint)));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = pairing.Control.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            await ssl.WriteAsync(new byte[] { 1 }.Concat(Convert.FromHexString(pairing.Key)).ToArray(), timeout.Token); var ack = new byte[1]; await ssl.ReadExactlyAsync(ack, timeout.Token);
            if (ack[0] != 1) throw new AuthenticationException("Relay did not accept this host pairing.");
            wire = new RelayWire(ssl); Connected = true; loop = ReadAsync(ssl, stop.Token); heartbeat = HeartbeatAsync(stop.Token);
            Status?.Invoke("Encrypted relay tunnel connected. Friends join " + new JoinAddress(pairing.Control.Host, pairing.GamePort));
        }
        catch { client.Dispose(); throw; }
    }
    private async Task ReadAsync(SslStream ssl, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await wire!.ReadAsync(ct); if (frame.Peer == 0) continue;
                foreach (var old in peers.Where(p => DateTime.UtcNow - p.Value.Seen > TimeSpan.FromMinutes(2)).ToArray()) if (peers.TryRemove(old.Key, out var removed)) removed.Socket.Dispose();
                if (!peers.TryGetValue(frame.Peer, out var peer))
                {
                    if (peers.Count >= 128) continue;
                    var socket = new UdpClient(new IPEndPoint(localAddress, 0)); socket.Connect(new IPEndPoint(localAddress, localPort)); peer = (socket, DateTime.UtcNow); peers[frame.Peer] = peer; _ = RepliesAsync(frame.Peer, socket, ct);
                }
                peers[frame.Peer] = (peer.Socket, DateTime.UtcNow); await peer.Socket.SendAsync(frame.Bytes, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException) { if (!ct.IsCancellationRequested) Status?.Invoke("Relay tunnel disconnected. Stop and start hosting to reconnect."); }
        finally { Connected = false; ssl.Dispose(); tcp?.Dispose(); foreach (var peer in peers.Values) peer.Socket.Dispose(); peers.Clear(); }
    }
    private async Task RepliesAsync(int id, UdpClient socket, CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { var packet = await socket.ReceiveAsync(ct); await wire!.WriteAsync(id, packet.Buffer, ct); } }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15)); while (await timer.WaitForNextTickAsync(ct)) await wire!.WriteAsync(0, ReadOnlyMemory<byte>.Empty, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); tcp?.Dispose(); foreach (var p in peers.Values) p.Socket.Dispose();
        foreach (var task in new[] { loop, heartbeat }.OfType<Task>()) try { await task; } catch (OperationCanceledException) { }
        Connected = false;
    }
}
