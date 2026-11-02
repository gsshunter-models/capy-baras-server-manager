using CapyServer.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

var root = Path.Combine(Path.GetTempPath(), "CapyServerTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var failures = 0;
async Task Test(string name, Func<Task> run) { try { await run(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); } }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
string Synthetic(string name, string dir)
{ var path = Path.Combine(root, name); File.WriteAllText(path, $"dir={dir}\nlongname=Test {dir}\nfirstlap=5\nnormallap=6\ngatecount=40\nhash={new string('a', 40)}\n"); return path; }
int UdpPort() { using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)u.Client.LocalEndPoint!).Port; }
int TcpPort() { var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); var p = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop(); return p; }
async Task Wait(Func<bool> ready) { var w = Stopwatch.StartNew(); while (!ready()) { if (w.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(); await Task.Delay(80); } }
try
{
    await Test("DuckDNS names, default game address, provider errors, secret handling and cancellation", DuckDnsTests.Run);
    var a = Synthetic("A.trackinfo", "TrackA"); var b = Synthetic("B.trackinfo", "TrackB");
    await Test("Trackinfo validation, import and deduplication", () =>
    {
        Check(TrackInfo.Read(a).Directory == "TrackA", "Read native fields");
        var original = File.ReadAllBytes(a); var imported = TrackInfo.Import(a, Path.Combine(root, "managed")); _ = TrackInfo.Import(a, Path.Combine(root, "managed"));
        Check(File.ReadAllBytes(a).SequenceEqual(original), "Original trackinfo unchanged"); Check(Directory.GetFiles(Path.GetDirectoryName(imported.Path)!).Length == 1, "Content deduplicated");
        var bad = Synthetic("Bad.trackinfo", "../escape"); Throws<InvalidDataException>(() => TrackInfo.Read(bad)); File.Delete(bad); return Task.CompletedTask;
    });
    ServerProfile Basic() { var p = new ServerProfile { ServerFolder = root, BindAddress = "127.0.0.1", AutoMap = false }; p.Rotation.Add(new() { Path = a, Name = "A", Minutes = 4, Laps = 2, Interval = 15 }); p.Rotation.Add(new() { Path = b, Name = "B", Minutes = 8, Laps = 3, Interval = -1 }); return p; }
    await Test("Native arguments, per-track overrides, output isolation and credentials", () =>
    {
        var p = Basic(); p.Rotation[0].NextTrack = 1; p.Options.First(o => o.Flag == "--password").Enabled = true; p.Options.First(o => o.Flag == "--password").Value = "synthetic-admin-secret";
        var folder = Path.Combine(root, "args-session"); var text = ServerSession.BuildArguments(p, Path.Combine(root, "profile"), folder, "/synthetic-control/", true);
        Check(!text.Contains("synthetic-admin-secret") && !text.Contains("/synthetic-control/"), "Preview secrets redacted");
        var argsFile = Path.Combine(folder, "args.txt"); File.WriteAllText(argsFile, text); var imported = NativeArguments.Import(argsFile, p);
        Check(imported.Rotation.Count == 2 && imported.Rotation[0].NextTrack == 1 && imported.Rotation[0].Minutes == 4 && imported.Rotation[1].Minutes == 8 && imported.Rotation[1].Laps == 3 && imported.Rotation[1].Interval == -1, "Native ordering preserves track overrides and next-track links");
        Check(!File.Exists(Path.Combine(root, "results.txt")), "Original server folder was not written");
        Throws<InvalidDataException>(() => NativeArguments.Quote("injected\n--extension foo")); Throws<InvalidDataException>(() => NativeArguments.Tokens("--greeting \"unclosed"));
        p.Port = 70000; Throws<InvalidDataException>(() => NativeArguments.Validate(p)); return Task.CompletedTask;
    });
    await Test("Profiles save/load and copied profiles have independent identities", () =>
    {
        var p = Basic(); var copy = ProfileStore.Copy(p); Check(copy.Id != p.Id, "Distinct profile storage"); copy.Rotation[0].Minutes = 99; Check(p.Rotation[0].Minutes == 4, "Independent rotation");
        var folder = Path.Combine(root, "profiles"); ProfileStore.Save(folder, new[] { p, copy }); Check(ProfileStore.Load(folder).Count == 2, "Profiles saved"); p.Id = "../unsafe"; Throws<InvalidDataException>(() => ProfileStore.Root(folder, p)); return Task.CompletedTask;
    });
    await Test("Join addresses and private relay pairing validate and stay separate", () =>
    {
        Check(JoinAddress.Parse("mxsimulator://example.com:19800").ToString() == "example.com:19800", "Native join URI");
        foreach (var invalid in new[] { "https://example.com:19800", "host:19800/args", "x@host:19800", "host:999999", "host:19800?key=secret" }) Throws<InvalidDataException>(() => JoinAddress.Parse(invalid));
        var pairing = new RelayPairing(new("localhost", 19802), 19801, new string('A', 64), new string('B', 64)); Check(RelayPairing.Parse(pairing.Encode()) == pairing, "Pairing roundtrip");
        Check(!new JoinAddress(pairing.Control.Host, pairing.GamePort).Link.Contains(pairing.Key), "Player invite omits host secret");
        foreach (var ip in new[] { "10.1.2.3", "192.168.1.1", "172.16.3.1", "100.64.0.1", "127.0.0.1", "169.254.1.1" }) Check(!RouterMapping.IsPublic(IPAddress.Parse(ip)), "Private and CGNAT addresses rejected"); return Task.CompletedTask;
    });
    await Test("TLS relay forwards two independent UDP clients bidirectionally and releases ports", async () =>
    {
        using var identity = RelayIdentity.Load(Path.Combine(root, "identity")); var game = UdpPort(); var publicUdp = UdpPort(); var control = TcpPort();
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, game)); using var end = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var destinations = new HashSet<int>();
        var echoLoop = Task.Run(async () => { try { while (!end.IsCancellationRequested) { var packet = await echo.ReceiveAsync(end.Token); destinations.Add(packet.RemoteEndPoint.Port); await echo.SendAsync(packet.Buffer, packet.RemoteEndPoint, end.Token); } } catch (OperationCanceledException) { } });
        await using (var relay = new PublicRelay(identity))
        {
            relay.Status += text => Console.WriteLine("  relay: " + text);
            relay.Start(IPAddress.Loopback, publicUdp, control); var pair = new RelayPairing(new("127.0.0.1", control), publicUdp, identity.Key, identity.Fingerprint);
            await using (var host = new HostTunnel(game))
            {
                await host.ConnectAsync(pair, end.Token); await Wait(() => relay.HostConnected);
                using var one = new UdpClient(); using var two = new UdpClient();
                foreach (var (client, text) in new[] { (one, "rider-one"), (two, "rider-two") })
                { var bytes = Encoding.UTF8.GetBytes(text); await client.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, publicUdp), end.Token); var result = await client.ReceiveAsync(end.Token); Check(result.Buffer.SequenceEqual(bytes), "Datagram roundtrip"); }
                Check(destinations.Count == 2, "Separate players have distinct native server source sockets"); Check(relay.ConnectedPeers == 2, "Relay tracks player endpoints");
                var room = new RoomInvite(pair.Control, publicUdp, identity.PlayerKey, identity.Fingerprint); Check(RoomInvite.Parse(room.Encode()) == room, "Room code roundtrip"); Check(!room.Encode().Contains(identity.Key), "Player room code does not reveal host control key");
                await using var bridgeOne = new PlayerBridge(); await using var bridgeTwo = new PlayerBridge();
                await bridgeOne.ConnectAsync(room, end.Token); await bridgeTwo.ConnectAsync(room, end.Token);
                using var localOne = new UdpClient(); using var localTwo = new UdpClient();
                async Task ThroughRoom(UdpClient client, PlayerBridge bridge, byte[] bytes)
                { await client.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, bridge.LocalPort), end.Token); var packet = await client.ReceiveAsync(end.Token); Check(packet.Buffer.SequenceEqual(bytes), "The in-app room carries complete UDP packets in both directions"); }
                await ThroughRoom(localOne, bridgeOne, Encoding.UTF8.GetBytes("room-player-one"));
                await ThroughRoom(localTwo, bridgeTwo, Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray());
                await using (var unauthorizedHost = new HostTunnel(game))
                {
                    var denied = false; try { await unauthorizedHost.ConnectAsync(pair with { Key = identity.PlayerKey }, end.Token); } catch (IOException) { denied = true; }
                    Check(denied && relay.HostConnected, "A player room invite cannot replace or authenticate as the private host");
                }
                await bridgeOne.DisposeAsync(); await Task.Delay(80); Check(relay.HostConnected, "A leaving player does not disconnect the host");
                await ThroughRoom(localTwo, bridgeTwo, Encoding.UTF8.GetBytes("remaining-player"));
            }
            await Wait(() => !relay.HostConnected);
            await using var wrong = new HostTunnel(game); var rejected = false;
            try { await wrong.ConnectAsync(pair with { Fingerprint = new string('0', 64) }, end.Token); } catch (AuthenticationException) { rejected = true; }
            Check(rejected, "An untrusted certificate is rejected");
        }
        await end.CancelAsync(); await echoLoop; using var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, publicUdp));
    });
    var native = Array.IndexOf(args, "--native");
    if (native >= 0 && native + 1 < args.Length)
    {
        await Test("Native mxserver loopback start, status, admin command acceptance and graceful stop", async () =>
        {
            var source = Path.GetFullPath(args[native + 1]); var infos = TrackInfo.Scan(Path.Combine(source, "trackinfo")).Take(2).ToArray(); Check(infos.Length == 2, "Real fixture tracks");
            var p = Basic(); p.ServerFolder = source; p.Port = UdpPort(); p.Rotation.Clear(); foreach (var info in infos) p.Rotation.Add(new() { Path = info.Path, Name = info.Name, Minutes = 3, Laps = 2 });
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var session = new ServerSession(); session.Log += line => messages.Enqueue(line);
            await session.StartAsync(p, Path.Combine(root, "native")); await Wait(() => session.Snapshot() != null);
            Check(session.Snapshot()!.Laps == 2 && session.Snapshot()!.Minutes == 3, "Native configured race settings are available before players connect");
            await session.CommandAsync("erode 2.5"); await session.CommandAsync("settrack 1"); await session.CommandAsync("restart"); await Task.Delay(300);
            await session.StopAsync(); Check(!session.Running, "Graceful shutdown");
            Check(messages.Any(l => l.Contains("\"erode\" is now 2.500000")), "Native server applied erode command");
            Check(messages.Any(l => l.Contains("Next track will be") && l.Contains("track-001.trackinfo")), "Zero-based next track accepted");
            Check(messages.Any(l => l.Contains("Server has requested a restart")), "Restart accepted; track initializes when riders join");
        });
    }
    Console.WriteLine(failures == 0 ? "All server manager checks passed." : failures + " checks failed."); return failures == 0 ? 0 : 1;
}
finally
{
    var target = Path.GetFullPath(root); var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("CapyServerTests-")) throw new InvalidOperationException("Unsafe test cleanup path.");
    Directory.Delete(target, true);
}
