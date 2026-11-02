using CapyServer.Core;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace CapyServer.Desktop;

public partial class MainWindow
{
    private string gameAddress = "", setupStatus = "Choose your DuckDNS name once. Automatic setup handles the game port, firewall, router and DNS.";
    private CancellationTokenSource? hostDnsStop, relayDnsStop;
    private Task? hostDnsTask, relayDnsTask;
    public string GameAddress { get => gameAddress; private set { gameAddress = value; Raise(); } }
    public string SetupStatus { get => setupStatus; private set { setupStatus = value; Raise(); } }
    public string DuckCredentialStatus => Profile != null && File.Exists(SecretStore.PathFor(state, Profile)) ? "DuckDNS token saved securely for this Windows account." : "DuckDNS token has not been saved yet.";
    private (DuckDnsName Name, string Token) DnsCredentials()
    {
        if (Profile == null) throw new InvalidOperationException("Choose a server profile.");
        var name = DuckDnsName.Parse(Profile.DuckDnsDomain); Profile.DuckDnsDomain = name.Label;
        if (!string.IsNullOrWhiteSpace(DuckToken.Password)) { SecretStore.Save(state, Profile, DuckToken.Password); DuckToken.Clear(); Raise(nameof(DuckCredentialStatus)); }
        var token = SecretStore.Read(state, Profile) ?? throw new InvalidDataException("Open DuckDNS, create your free name and save its account token here once. The manager will remember it securely.");
        Save(); return (name, token);
    }
    private static IPAddress? PublicLocalAddress() => RouterMapping.LocalAddresses().Select(IPAddress.Parse).FirstOrDefault(RouterMapping.IsPublic);
    private static int FreePrivatePort()
    { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private async Task StartAutomaticAsync()
    {
        if (server.Running) throw new InvalidOperationException("The session is already running. Close it before changing automatic setup.");
        if (Profile is not { } current) return;
        Tabs.SelectedIndex = 3; GameAddress = "";
        var p = ProfileStore.Copy(current); p.Id = current.Id; p.Name = current.Name;
        if (relay != null && pairing is { GamePort: JoinAddress.DefaultPort })
        { p.UseRelay = true; p.RelayPairing = pairing.Encode(); current.UseRelay = true; current.RelayPairing = p.RelayPairing; Raise(nameof(Profile)); }
        NativeArguments.Validate(p);
        if (!File.Exists(Path.Combine(p.ServerFolder, "mxserver.exe"))) throw new FileNotFoundException("Choose the folder containing mxserver.exe on Host first.");
        try
        {
            if (p.UseRelay)
            {
                var paired = RelayPairing.Parse(p.RelayPairing);
                if (paired.GamePort != JoinAddress.DefaultPort) throw new InvalidDataException("On the public backend, click Automatic relay setup, then copy its updated host pairing here. That reserves the default game port so friends only enter a name.");
                SetupStatus = "Connecting through the configured room backend…";
                p.Port = relay != null && identity != null && paired.Key == identity.Key ? FreePrivatePort() : JoinAddress.DefaultPort; p.BindAddress = "127.0.0.1"; p.AutoMap = false;
                await StartManualAsync(p, true);
                if (!server.Running) { SetupStatus = "Setup cancelled. No session was started."; return; }
                var advertised = paired.Control.Host;
                if (!string.IsNullOrWhiteSpace(current.DuckDnsDomain) && !DuckDnsName.Parse(current.DuckDnsDomain).Hostname.Equals(advertised, StringComparison.OrdinalIgnoreCase))
                {
                    var creds = DnsCredentials(); var addresses = await Dns.GetHostAddressesAsync(advertised).WaitAsync(TimeSpan.FromSeconds(8));
                    var ip = addresses.FirstOrDefault(RouterMapping.IsPublic) ?? throw new IOException("The backend does not resolve to a public IPv4 address.");
                    using var http = DuckDnsClient.CreateHttpClient(); var updated = await new DuckDnsClient(http).UpdateAsync(creds.Name, creds.Token, ip); advertised = updated.Hostname;
                    StartDnsMonitor(creds.Name, creds.Token, async ct => (await Dns.GetHostAddressesAsync(paired.Control.Host, ct)).FirstOrDefault(RouterMapping.IsPublic) ?? throw new IOException("Backend DNS is unavailable."), false);
                }
                GameAddress = new JoinAddress(advertised, JoinAddress.DefaultPort).GameAddress;
            }
            else
            {
                var creds = DnsCredentials(); p.Port = JoinAddress.DefaultPort; p.BindAddress = "0.0.0.0"; p.AutoMap = false;
                current.Port = p.Port; current.BindAddress = p.BindAddress; Raise(nameof(Profile));
                SetupStatus = "1/4 · Setting up the Windows game firewall rule. Windows may ask for permission once…";
                await Firewall.EnsureAsync(p.Port);
                SetupStatus = "2/4 · Reserving the default game connection automatically…";
                var address = PublicLocalAddress(); Func<CancellationToken, Task<IPAddress>> addressSource;
                if (address == null)
                {
                    var map = new RouterMapping(); maps.Add(map); await map.OpenAsync(p.Port, publicPort: JoinAddress.DefaultPort);
                    address = map.ExternalAddress; addressSource = map.RefreshAddressAsync;
                }
                else addressSource = _ => Task.FromResult(PublicLocalAddress() ?? throw new IOException("The public network address is unavailable."));
                if (address == null || !RouterMapping.IsPublic(address)) throw new IOException("This connection is behind CGNAT or another private router. Use a publicly reachable backend with Automatic relay setup; DNS alone cannot open an incoming connection.");
                SetupStatus = "3/4 · Updating " + creds.Name.Hostname + "…";
                using var http = DuckDnsClient.CreateHttpClient(); await new DuckDnsClient(http).UpdateAsync(creds.Name, creds.Token, address);
                SetupStatus = "4/4 · Starting the native server…";
                await StartManualAsync(p, true); if (!server.Running) { await CleanupConnectionAsync(); SetupStatus = "Setup cancelled. No session was started."; return; }
                GameAddress = creds.Name.Hostname; JoinText = GameAddress;
                StartDnsMonitor(creds.Name, creds.Token, addressSource, false);
            }
            SetupStatus = "Hosted. Friends can enter " + GameAddress + " in MX Simulator. No port number is needed. First-time DNS changes may take a moment; have a friend test from outside your network.";
            ConnectionStatus = "Named game address configured. Outside reachability has not been independently tested.";
            Notice = "Copy game address to share the name. Matching track files are still required.";
        }
        catch (Exception ex)
        {
            if (server.Running) await server.StopAsync(); await CleanupConnectionAsync(); SetupStatus = "Automatic setup needs attention: " + ex.Message; throw;
        }
    }
    private void StartDnsMonitor(DuckDnsName name, string token, Func<CancellationToken, Task<IPAddress>> address, bool forRelay)
    {
        var cancellation = new CancellationTokenSource(); var ct = cancellation.Token;
        async Task Monitor()
        {
            using var http = DuckDnsClient.CreateHttpClient(); using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { var ip = await address(ct); await new DuckDnsClient(http).UpdateAsync(name, token, ip, ct); }
                    catch (Exception ex) when (ex is IOException or TimeoutException or SocketException) { _ = Dispatcher.BeginInvoke(() => SetupStatus = "DNS refresh needs attention. Your name may be stale; rerun setup when the connection is available."); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        if (forRelay) { relayDnsStop = cancellation; relayDnsTask = Monitor(); } else { hostDnsStop = cancellation; hostDnsTask = Monitor(); }
    }
    private async Task StopDnsMonitorAsync(bool forRelay)
    {
        var cancellation = forRelay ? relayDnsStop : hostDnsStop; var task = forRelay ? relayDnsTask : hostDnsTask;
        if (cancellation != null) { await cancellation.CancelAsync(); if (task != null) await task; cancellation.Dispose(); }
        if (forRelay) { relayDnsStop = null; relayDnsTask = null; } else { hostDnsStop = null; hostDnsTask = null; }
    }
    private async void SaveDns_Click(object sender, RoutedEventArgs e) => await Run(() => { _ = DnsCredentials(); SetupStatus = "Name and encrypted token saved. Click Set up & host to finish automatically."; return Task.CompletedTask; });
    private void OpenDuckDns_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://www.duckdns.org/") { UseShellExecute = true });
    private async void AutoHost_Click(object sender, RoutedEventArgs e) => await Run(async () => { Profile!.AutomaticSetup = true; await StartAutomaticAsync(); });
    private async void CopyGameAddress_Click(object sender, RoutedEventArgs e) => await Run(() => { if (string.IsNullOrWhiteSpace(GameAddress)) throw new InvalidOperationException("Finish automatic setup first."); Clipboard.SetText(GameAddress); Notice = "Copied the game address. Friends paste this name into MX Simulator's Connect screen."; return Task.CompletedTask; });
    private async void AutoRelay_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (relay != null || relayMaps.Count != 0) throw new InvalidOperationException("Stop the existing relay before automatic relay setup.");
        var creds = DnsCredentials(); RouterMapping? udp = null, tcp = null;
        try
        {
            SetupStatus = "Setting up the public backend automatically…"; RelayUdp.Text = JoinAddress.DefaultPort.ToString(); RelayTcp.Text = "19802";
            await Firewall.EnsureAsync(JoinAddress.DefaultPort, 19802);
            var ip = PublicLocalAddress(); Func<CancellationToken, Task<IPAddress>> source;
            if (ip == null)
            {
                udp = new RouterMapping(); tcp = new RouterMapping(); relayMaps.Add(udp); relayMaps.Add(tcp);
                await udp.OpenAsync(JoinAddress.DefaultPort, publicPort: JoinAddress.DefaultPort); await tcp.OpenAsync(19802, Mono.Nat.Protocol.Tcp, publicPort: 19802);
                ip = udp.ExternalAddress; source = udp.RefreshAddressAsync;
            }
            else source = _ => Task.FromResult(PublicLocalAddress() ?? throw new IOException("Public backend address is unavailable."));
            if (ip == null || !RouterMapping.IsPublic(ip)) throw new IOException("This backend is also behind CGNAT or a private upstream router. It must have a reachable public connection.");
            using var http = DuckDnsClient.CreateHttpClient(); await new DuckDnsClient(http).UpdateAsync(creds.Name, creds.Token, ip);
            identity ??= RelayIdentity.Load(Path.Combine(state, "relay")); relay = new PublicRelay(identity); relay.Status += text => Dispatcher.BeginInvoke(() => RelayStatus = text);
            relay.Start(IPAddress.Any, JoinAddress.DefaultPort, 19802); RelayHostName.Text = creds.Name.Hostname;
            pairing = new(new(creds.Name.Hostname, 19802), JoinAddress.DefaultPort, identity.Key, identity.Fingerprint); RelayJoinText = creds.Name.Hostname;
            Profile!.RelayPairing = pairing.Encode(); Profile.UseRelay = true; Raise(nameof(Profile)); Save(); SaveRelaySettings();
            StartDnsMonitor(creds.Name, creds.Token, source, true);
            SetupStatus = "Public backend configured. Create a session here, or copy its private host pairing to the remote host. Players enter " + creds.Name.Hostname + " in game.";
        }
        catch { await StopRelayAsync(); throw; }
    });
}
