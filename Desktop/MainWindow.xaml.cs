using CapyServer.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CapyServer.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<ServerProfile> Profiles { get; } = [];
    public ObservableCollection<TrackInfo> FilteredTracks { get; } = [];
    public ObservableCollection<PlayerSnapshot> Players { get; } = [];
    private List<TrackInfo> tracks = [];
    private ServerProfile? profile;
    public ServerProfile? Profile { get => profile; set { if (profile == value) return; profile = value; Raise(); if (DuckToken != null) DuckToken.Clear(); Raise(nameof(DuckCredentialStatus)); if (IsLoaded) { WatchGame(); _ = Run(ScanAsync); } } }
    private string status = "Stopped", liveTrack = "No session", count = "0 connected", connection = "Not connected. Create a session, then copy its invite.", join = "", relayStatus = "Relay stopped", relayJoin = "", notice = "Original server and game files are kept in place.", details = "", log = "", roomStatus = "No joined session";
    public string StatusText { get => status; private set { status = value; Raise(); } }
    public string LiveTrack { get => liveTrack; private set { liveTrack = value; Raise(); } }
    public string PlayerCount { get => count; private set { count = value; Raise(); } }
    public string ConnectionStatus { get => connection; private set { connection = value; Raise(); } }
    public string JoinText { get => join; set { join = value; Raise(); } }
    public string RelayStatus { get => relayStatus; private set { relayStatus = value; Raise(); } }
    public string RelayJoinText { get => relayJoin; private set { relayJoin = value; Raise(); } }
    public string Notice { get => notice; private set { notice = value; Raise(); } }
    public string LiveDetails { get => details; private set { details = value; Raise(); } }
    public string LogText { get => log; private set { log = value; Raise(); } }
    public string RoomStatus { get => roomStatus; private set { roomStatus = value; Raise(); } }
    public string LanAddresses => "LAN addresses: " + string.Join(", ", RouterMapping.LocalAddresses());
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly string state;
    private readonly ServerSession server = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> logLines = new();
    private readonly List<RouterMapping> maps = [];
    private readonly List<RouterMapping> relayMaps = [];
    private HostTunnel? tunnel;
    private PlayerBridge? playerBridge;
    private PublicRelay? relay;
    private RelayIdentity? identity;
    private RelayPairing? pairing;
    private FileSystemWatcher? watcher;
    private DateTime exportChanged;
    private bool busy, closing;
    private List<RotationEntry> activeRotation = [];
    private int activePort;
    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs(); var idx = Array.IndexOf(args, "--state");
        state = idx >= 0 && idx + 1 < args.Length ? Path.GetFullPath(args[idx + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapyBaraServerManager");
        Directory.CreateDirectory(state); DataContext = this;
        server.Log += line => Dispatcher.BeginInvoke(() => AddLog(line));
        server.Exited += () => Dispatcher.BeginInvoke(() => { StatusText = "Stopped"; UpdateButtons(); if (!closing) _ = Run(CleanupConnectionAsync); });
        timer.Tick += async (_, _) =>
        {
            if (server.Running)
            {
                var snapshot = server.Snapshot();
                if (snapshot != null) { LiveTrack = string.IsNullOrEmpty(snapshot.Track) ? "Waiting for first rider" : snapshot.Track; PlayerCount = snapshot.Players.Count + " connected"; LiveDetails = string.IsNullOrEmpty(snapshot.Track) ? "Native server is ready. Race and terrain initialize when riders connect." : $"Erode {snapshot.Erode:0.###} · {snapshot.Minutes:0.#} minutes + {snapshot.Laps} laps · elapsed {snapshot.RaceSeconds:0}s"; Players.Clear(); foreach (var p in snapshot.Players) Players.Add(p); }
                if (tunnel != null && !tunnel.Connected) ConnectionStatus = "Relay tunnel disconnected. Stop and start hosting to reconnect.";
            }
            if (relay != null) RelayStatus = relay.HostConnected ? $"Private host connected · {relay.ConnectedPeers} active player endpoints" : "Relay listening · waiting for a private host";
            if (playerBridge != null && !playerBridge.Connected) RoomStatus = "Room disconnected. Join the session again to reconnect.";
            if (exportChanged != default && DateTime.UtcNow - exportChanged > TimeSpan.FromSeconds(1)) { exportChanged = default; if (AutoImport.IsChecked == true && !busy) await Run(ImportGameAsync); }
        };
        Loaded += async (_, _) => { await Run(InitializeAsync); var render = Array.IndexOf(args, "--render"); if (idx >= 0 && render >= 0 && render + 1 < args.Length) await RenderPreviewAsync(args[render + 1]); };
        Closing += OnClosing;
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateButtons();
        try { CommitEditors(); await action(); }
        catch (Exception ex) { if (!server.Running) StatusText = "Stopped"; Notice = ex.Message; AddLog(ex.Message); MessageBox.Show(this, ex.Message, "Capy Bara's Server Manager", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { busy = false; UpdateButtons(); }
    }
    private void CommitEditors() { RotationGrid.CommitEdit(); OptionsGrid.CommitEdit(); if (Errors(this)) throw new InvalidDataException("Correct the highlighted setting before continuing."); }
    private static bool Errors(DependencyObject parent)
    { if (Validation.GetHasError(parent)) return true; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (Errors(VisualTreeHelper.GetChild(parent, i))) return true; return false; }
    private void UpdateButtons()
    { StartButton.IsEnabled = !busy && !server.Running; StopButton.IsEnabled = !busy && server.Running; ProfilesBox.IsEnabled = !busy && !server.Running; RelayStartButton.IsEnabled = !busy && relay == null; }
    private void AddLog(string text)
    { logLines.Enqueue(DateTime.Now.ToString("HH:mm:ss ") + text); while (logLines.Count > 250) logLines.Dequeue(); LogText = string.Join('\n', logLines); }
    private async Task InitializeAsync()
    {
        foreach (var p in ProfileStore.Load(state)) Profiles.Add(p);
        if (Profiles.Count == 0)
        {
            var p = new ServerProfile(); var args = Path.Combine(p.ServerFolder, "serverargs.txt");
            if (File.Exists(args))
            {
                try { p = NativeArguments.Import(args, p); p.Name = "My existing server"; var bat = Path.Combine(p.ServerFolder, "runserver.bat"); if (File.Exists(bat)) { var match = Regex.Match(File.ReadAllText(bat), @"(?im)^\s*set\s+ADMINID=(\d+)\s*$"); if (match.Success) p.AdminUid = match.Groups[1].Value; } }
                catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) { AddLog("Existing args were not imported: " + ex.Message); }
            }
            Profiles.Add(p); Save();
        }
        Profile = Profiles[0]; RelayHostName.Text = RouterMapping.LocalAddresses().FirstOrDefault(a => RouterMapping.IsPublic(IPAddress.Parse(a))) ?? "";
        var relaySettings = Path.Combine(state, "relay-settings.json");
        if (File.Exists(relaySettings)) { var saved = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(relaySettings)); if (saved is { Length: 3 }) { RelayHostName.Text = saved[0]; RelayUdp.Text = saved[1]; RelayTcp.Text = saved[2]; } }
        WatchGame(); timer.Start(); await ScanAsync(); Notice = $"Ready · {tracks.Count} valid trackinfos found. No server has been started.";
    }
    private void Save() { ProfileStore.Save(state, Profiles); ProfilesBox.Items.Refresh(); Notice = "Profiles saved locally."; }
    private async Task ScanAsync()
    {
        if (Profile is not { } current) return;
        var p = ProfileStore.Copy(current); p.Id = current.Id; p.Name = current.Name;
        tracks = await Task.Run(() => TrackInfo.Scan(Path.Combine(state, "trackinfos"), Path.Combine(p.ServerFolder, "trackinfo"), p.GameFolder));
        FilterTracks(); Notice = $"{tracks.Count} trackinfos ready. Select tracks and add them to the rotation.";
    }
    private void FilterTracks()
    { var search = TrackSearch.Text.Trim(); FilteredTracks.Clear(); foreach (var t in tracks.Where(t => (t.Name + " " + t.Directory).Contains(search, StringComparison.OrdinalIgnoreCase))) FilteredTracks.Add(t); }
    private void WatchGame()
    {
        watcher?.Dispose(); watcher = null; if (Profile == null || !Directory.Exists(Profile.GameFolder)) return;
        watcher = new FileSystemWatcher(Profile.GameFolder, "*.trackinfo") { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        void Changed(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() => exportChanged = DateTime.UtcNow);
        watcher.Created += Changed; watcher.Changed += Changed; watcher.Renamed += (s, e) => Changed(s, e);
    }
    private async Task ImportGameAsync()
    {
        if (Profile == null || !Directory.Exists(Profile.GameFolder)) return;
        var imported = await Task.Run(() => { var n = 0; foreach (var path in Directory.EnumerateFiles(Profile.GameFolder, "*.trackinfo")) try { _ = TrackInfo.Import(path, state); n++; } catch (IOException) { } catch (InvalidDataException) { } return n; });
        await ScanAsync(); Notice = $"Imported {imported} game trackinfos into the manager. Game files were left in place.";
    }
    private Task StartAsync() => Profile?.AutomaticSetup == true ? StartAutomaticAsync() : StartManualAsync();
    private async Task StartManualAsync(ServerProfile? prepared = null, bool preparedNetwork = false)
    {
        if (Profile is not { } current) return;
        var startingRows = current.Rotation.Where(r => r.Enabled).ToList();
        var p = prepared ?? ProfileStore.Copy(current); p.Id = current.Id; p.Name = current.Name;
        if (p.Options.Any(o => o.Enabled && OptionCatalog.Specs.Any(s => s.Flag == o.Flag && s.Executable)) && MessageBox.Show(this, "This profile enables custom JavaScript, command extensions, or a results command. Start with those executable options?", "Custom server commands", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var relayPairing = p.UseRelay ? RelayPairing.Parse(p.RelayPairing) : null;
        if (relayPairing != null && p.BindAddress != "0.0.0.0" && p.BindAddress != "127.0.0.1") throw new InvalidDataException("Relay hosting needs bind address 127.0.0.1 or 0.0.0.0.");
        Save(); StatusText = "Starting…"; await server.StartAsync(p, state); activePort = p.Port; activeRotation = startingRows; StatusText = "Running";
        if (relayPairing != null)
        {
            try
            {
                tunnel = new HostTunnel(p.Port); tunnel.Status += s => Dispatcher.BeginInvoke(() => { ConnectionStatus = s; AddLog(s); });
                var target = relay != null && identity != null && relayPairing.Key == identity.Key ? relayPairing with { Control = new JoinAddress("127.0.0.1", RelayPorts().Tcp) } : relayPairing;
                await tunnel.ConnectAsync(target); JoinText = new RoomInvite(relayPairing.Control, relayPairing.GamePort, RoomInvite.DerivePlayerKey(relayPairing.Key), relayPairing.Fingerprint).Encode();
                ConnectionStatus = "Capy room network connected. Copy the room invite; friends join inside this app.";
            }
            catch { if (tunnel != null) await tunnel.DisposeAsync(); tunnel = null; await server.StopAsync(); throw; }
        }
        else
        {
            JoinText = new JoinAddress(p.BindAddress != "0.0.0.0" ? p.BindAddress : RouterMapping.LocalAddresses().FirstOrDefault() ?? "127.0.0.1", p.Port).ToString();
            ConnectionStatus = p.BindAddress == "127.0.0.1" ? "Loopback-only host. Only this PC can join directly." : "Local server ready. LAN players can use the LAN address; outside players need a reachable public UDP address.";
            if (!preparedNetwork && p.AutoMap && p.BindAddress == "0.0.0.0")
            {
                var map = new RouterMapping(); map.Status += s => Dispatcher.BeginInvoke(() => ConnectionStatus = s); maps.Add(map);
                try { await map.OpenAsync(p.Port); if (map.ExternalAddress != null && RouterMapping.IsPublic(map.ExternalAddress)) JoinText = new JoinAddress(map.ExternalAddress.ToString(), map.PublicPort).ToString(); }
                catch (Exception ex) { ConnectionStatus = "Server is running locally. Automatic forwarding: " + ex.Message; }
            }
        }
        if (!server.Running) { await CleanupConnectionAsync(); throw new IOException("The server exited during connection setup. Check its logs."); }
        Notice = "Hosting. Use Connect to copy the invite, and Live controls to manage the race.";
    }
    private async Task CleanupConnectionAsync()
    {
        await StopDnsMonitorAsync(false); GameAddress = "";
        if (tunnel != null) { await tunnel.DisposeAsync(); tunnel = null; }
        foreach (var map in maps) await map.DisposeAsync(); maps.Clear(); ConnectionStatus = "Host connection closed. Owned router mappings released.";
    }
    private async Task StopAsync() { await server.StopAsync(); await CleanupConnectionAsync(); StatusText = "Stopped"; Players.Clear(); PlayerCount = "0 connected"; Notice = "Server stopped. Profile and session files are preserved."; }
    private async void Start_Click(object s, RoutedEventArgs e) => await Run(StartAsync);
    private async void Stop_Click(object s, RoutedEventArgs e) => await Run(StopAsync);
    private async void Save_Click(object s, RoutedEventArgs e) => await Run(() => { Save(); return Task.CompletedTask; });
    private async void NewProfile_Click(object s, RoutedEventArgs e) => await Run(() => { if (server.Running) throw new InvalidOperationException("Stop hosting before switching profiles."); var p = new ServerProfile { Name = "New server " + (Profiles.Count + 1) }; Profiles.Add(p); Profile = p; Save(); return Task.CompletedTask; });
    private async void DuplicateProfile_Click(object s, RoutedEventArgs e) => await Run(() => { if (server.Running || Profile == null) throw new InvalidOperationException("Stop hosting before switching profiles."); var p = ProfileStore.Copy(Profile); Profiles.Add(p); Profile = p; Save(); return Task.CompletedTask; });
    private async void ImportArgs_Click(object s, RoutedEventArgs e) => await Run(async () => { if (server.Running || Profile == null) throw new InvalidOperationException("Stop hosting before importing a profile."); var dialog = new OpenFileDialog { Title = "Import native server arguments", Filter = "Server argument files|*.txt;*.args|All files|*.*", InitialDirectory = Profile.ServerFolder }; if (dialog.ShowDialog(this) != true) return; var p = NativeArguments.Import(dialog.FileName, Profile); Profiles.Add(p); Profile = p; Save(); await ScanAsync(); });
    private async void ChooseServer_Click(object s, RoutedEventArgs e) => await Run(async () => { var d = new OpenFolderDialog { Title = "Choose mxserver folder" }; if (d.ShowDialog(this) == true) { Profile!.ServerFolder = d.FolderName; Raise(nameof(Profile)); Save(); await ScanAsync(); } });
    private async void ChooseGame_Click(object s, RoutedEventArgs e) => await Run(async () => { var d = new OpenFolderDialog { Title = "Choose MX Simulator personal folder" }; if (d.ShowDialog(this) == true) { Profile!.GameFolder = d.FolderName; Raise(nameof(Profile)); Save(); WatchGame(); await ScanAsync(); } });
    private async void ChooseExe_Click(object s, RoutedEventArgs e) => await Run(() => { var d = new OpenFileDialog { Title = "Choose mx.exe", Filter = "MX Simulator|mx.exe" }; if (d.ShowDialog(this) == true) { Profile!.GameExecutable = d.FileName; Raise(nameof(Profile)); Save(); } return Task.CompletedTask; });
    private async void Scan_Click(object s, RoutedEventArgs e) => await Run(async () => { await ImportGameAsync(); await ScanAsync(); });
    private void TrackSearch_Changed(object s, TextChangedEventArgs e) { if (IsLoaded) FilterTracks(); }
    private async void ImportTrack_Click(object s, RoutedEventArgs e) => await Run(async () => { var d = new OpenFileDialog { Filter = "MX Simulator trackinfo|*.trackinfo", Multiselect = true }; if (d.ShowDialog(this) != true) return; foreach (var path in d.FileNames) _ = TrackInfo.Import(path, state); await ScanAsync(); });
    private void CopyExport_Click(object s, RoutedEventArgs e) { Clipboard.SetText("/savetrackinfo"); Notice = "Copied. Load the track in MX Simulator, press T, paste /savetrackinfo and send it. The manager watches for its export."; }
    private async void AddTrack_Click(object s, RoutedEventArgs e) => await Run(() => { foreach (var track in TrackLibrary.SelectedItems.OfType<TrackInfo>()) Profile!.Rotation.Add(new() { Path = track.Path, Name = track.Name, Laps = Profile.Laps, Minutes = Profile.Minutes, Interval = Profile.TrackInterval }); Save(); return Task.CompletedTask; });
    private void Move(int delta) { if (Profile == null || RotationGrid.SelectedItem is not RotationEntry row) return; var index = Profile.Rotation.IndexOf(row); var next = index + delta; if (next >= 0 && next < Profile.Rotation.Count) Profile.Rotation.Move(index, next); }
    private void MoveUp_Click(object s, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object s, RoutedEventArgs e) => Move(1);
    private void RemoveTrack_Click(object s, RoutedEventArgs e) { if (RotationGrid.SelectedItem is RotationEntry row) Profile?.Rotation.Remove(row); }
    private async void SwitchTrack_Click(object s, RoutedEventArgs e) => await Run(async () => { if (RotationGrid.SelectedItem is not RotationEntry row) throw new InvalidOperationException("Select a rotation row."); var index = activeRotation.IndexOf(row); if (index < 0) throw new InvalidOperationException("This track was not in the running rotation. Restart the server to load rotation changes."); if (MessageBox.Show(this, "Queue " + row.Name + " and request a race restart?", "Switch track", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await server.CommandAsync("settrack " + index); await server.CommandAsync("restart"); Notice = "Track queued and race restart requested. Rotation edits after startup require a server restart."; });
    private async void CommandButton_Click(object s, RoutedEventArgs e) => await Run(() => server.CommandAsync(((Button)s).Tag.ToString()!));
    private async void Erode_Click(object s, RoutedEventArgs e) => await Run(async () => { if (Profile == null || !double.IsFinite(Profile.Erode) || Profile.Erode is < 0 or > 100) throw new InvalidDataException("Erode must be 0–100."); await server.CommandAsync("erode " + Profile.Erode.ToString(CultureInfo.InvariantCulture)); });
    private async void SendCommand_Click(object s, RoutedEventArgs e) => await Run(() => server.CommandAsync(CommandBox.Text));
    private async void Kick_Click(object s, RoutedEventArgs e) => await Run(async () => { if (PlayersGrid.SelectedItem is not PlayerSnapshot p) throw new InvalidOperationException("Select a player."); if (MessageBox.Show(this, "Kick UID " + p.Uid + " from this session?", "Kick player", MessageBoxButton.YesNo) == MessageBoxResult.Yes) await server.CommandAsync("kick " + p.Slot); });
    private async void PlayerCommand_Click(object s, RoutedEventArgs e) => await Run(async () => { if (PlayersGrid.SelectedItem is not PlayerSnapshot p) throw new InvalidOperationException("Select a player."); await server.CommandAsync(((Button)s).Tag + " " + p.Uid); });
    private async void CopyInvite_Click(object s, RoutedEventArgs e) => await Run(() => { Clipboard.SetText(JoinText.StartsWith("capyroom://", StringComparison.OrdinalIgnoreCase) ? RoomInvite.Parse(JoinText).Encode() : JoinAddress.Parse(JoinText).Link); Notice = "Session invite copied. Friends paste it into Join session in this app."; return Task.CompletedTask; });
    private async Task JoinAsync(string address)
    {
        var endpoint = JoinAddress.Parse(address); if (Profile == null || !File.Exists(Profile.GameExecutable)) throw new FileNotFoundException("Choose mx.exe on the Host tab first.");
        using var check = Process.GetProcessesByName("mx").FirstOrDefault();
        if (check != null) { Clipboard.SetText(endpoint.ToString()); MessageBox.Show(this, "MX Simulator is already open. The address was copied; paste it into the game's multiplayer Connect screen."); return; }
        var start = new ProcessStartInfo(Profile.GameExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Profile.GameExecutable)! }; start.ArgumentList.Add("--connect"); start.ArgumentList.Add(endpoint.ToString()); Process.Start(start); await Task.CompletedTask;
    }
    private async void JoinLocal_Click(object s, RoutedEventArgs e) => await Run(() => JoinAsync("127.0.0.1:" + (server.Running ? activePort : Profile!.Port)));
    private async void JoinFriend_Click(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (!FriendAddress.Text.Trim().StartsWith("capyroom://", StringComparison.OrdinalIgnoreCase)) { await JoinAsync(FriendAddress.Text); return; }
        var invite = RoomInvite.Parse(FriendAddress.Text); if (playerBridge != null) await playerBridge.DisposeAsync();
        playerBridge = new PlayerBridge(); playerBridge.Status += text => Dispatcher.BeginInvoke(() => RoomStatus = text);
        try { await playerBridge.ConnectAsync(invite); RoomStatus = "Joined the room. Game traffic is carried inside the manager; keep this app open."; await JoinAsync("127.0.0.1:" + playerBridge.LocalPort); }
        catch { await playerBridge.DisposeAsync(); playerBridge = null; RoomStatus = "Session join failed"; throw; }
    });
    private async void LeaveRoom_Click(object s, RoutedEventArgs e) => await Run(async () => { if (playerBridge != null) await playerBridge.DisposeAsync(); playerBridge = null; RoomStatus = "Left the session"; });
    private async void HostFirewall_Click(object s, RoutedEventArgs e) => await Run(async () => { await Firewall.ConfigureAsync(Profile!.Port, null); Notice = "Host UDP firewall rule configured. Router/public reachability is separate."; });
    private async void RemoveHostFirewall_Click(object s, RoutedEventArgs e) => await Run(() => Firewall.ConfigureAsync(Profile!.Port, null, true));
    private (int Udp, int Tcp) RelayPorts()
    { if (!int.TryParse(RelayUdp.Text, out var udp) || !int.TryParse(RelayTcp.Text, out var tcp) || udp is < 1024 or > 65535 || tcp is < 1024 or > 65535) throw new InvalidDataException("Relay ports must be 1024–65535."); return (udp, tcp); }
    private async void RelayStart_Click(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (relay != null) throw new InvalidOperationException("Relay is already running."); var ports = RelayPorts();
        var address = JoinAddress.Parse(RelayHostName.Text.Trim() + ":" + ports.Tcp); identity ??= RelayIdentity.Load(Path.Combine(state, "relay"));
        relay = new PublicRelay(identity); relay.Status += text => Dispatcher.BeginInvoke(() => { RelayStatus = text; AddLog(text); });
        try { relay.Start(IPAddress.Any, ports.Udp, ports.Tcp); pairing = new(new(address.Host, relayMaps.Count == 2 ? relayMaps[1].PublicPort : ports.Tcp), relayMaps.Count == 2 ? relayMaps[0].PublicPort : ports.Udp, identity.Key, identity.Fingerprint); RelayJoinText = new JoinAddress(address.Host, pairing.GamePort).ToString(); SaveRelaySettings(); RelayStatus = "Relay listening. Allow UDP and TCP through the firewall and router on this public machine."; }
        catch { await relay.DisposeAsync(); relay = null; throw; }
    });
    private async Task StopRelayAsync() { await StopDnsMonitorAsync(true); if (relay != null) await relay.DisposeAsync(); relay = null; pairing = null; foreach (var map in relayMaps) await map.DisposeAsync(); relayMaps.Clear(); RelayStatus = "Relay stopped"; }
    private async void RelayStop_Click(object s, RoutedEventArgs e) => await Run(StopRelayAsync);
    private async void RelayMap_Click(object s, RoutedEventArgs e) => await Run(async () =>
    {
        if (relayMaps.Count > 0) throw new InvalidOperationException("Relay mappings already exist. Stop the relay to remove them before mapping again.");
        var ports = RelayPorts(); var udp = new RouterMapping(); var tcp = new RouterMapping(); relayMaps.Add(udp); relayMaps.Add(tcp);
        try
        {
            await udp.OpenAsync(ports.Udp); await tcp.OpenAsync(ports.Tcp, Mono.Nat.Protocol.Tcp);
            if (udp.ExternalAddress == null || !RouterMapping.IsPublic(udp.ExternalAddress)) throw new IOException("This relay machine is also behind an upstream private network. Use a public PC/VPS for the relay.");
            RelayHostName.Text = udp.ExternalAddress.ToString(); identity ??= RelayIdentity.Load(Path.Combine(state, "relay"));
            pairing = new(new(RelayHostName.Text, tcp.PublicPort), udp.PublicPort, identity.Key, identity.Fingerprint); RelayJoinText = new JoinAddress(RelayHostName.Text, udp.PublicPort).ToString(); RelayStatus = "Relay UDP and TCP router mappings created. Outside reachability still needs a friend to test.";
        }
        catch { foreach (var map in relayMaps) await map.DisposeAsync(); relayMaps.Clear(); throw; }
    });
    private async void RelayFirewall_Click(object s, RoutedEventArgs e) => await Run(() => { var p = RelayPorts(); return Firewall.ConfigureAsync(p.Udp, p.Tcp); });
    private async void RemoveRelayFirewall_Click(object s, RoutedEventArgs e) => await Run(() => { var p = RelayPorts(); return Firewall.ConfigureAsync(p.Udp, p.Tcp, true); });
    private async void CopyPairing_Click(object s, RoutedEventArgs e) => await Run(() => { if (pairing == null || relay == null) throw new InvalidOperationException("Start the relay first."); Clipboard.SetText(pairing.Encode()); Notice = "Private host pairing copied. Do not give this code to players."; return Task.CompletedTask; });
    private async void CopyRelayInvite_Click(object s, RoutedEventArgs e) => await Run(() => { if (pairing == null || identity == null || relay == null) throw new InvalidOperationException("Start the relay first."); Clipboard.SetText(new RoomInvite(pairing.Control, pairing.GamePort, identity.PlayerKey, identity.Fingerprint).Encode()); Notice = "Player room invite copied. It does not contain the private host pairing key."; return Task.CompletedTask; });
    private void OpenSession_Click(object s, RoutedEventArgs e) { var path = string.IsNullOrEmpty(server.SessionFolder) ? state : server.SessionFolder; Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private void SaveRelaySettings() => File.WriteAllText(Path.Combine(state, "relay-settings.json"), System.Text.Json.JsonSerializer.Serialize(new[] { RelayHostName.Text, RelayUdp.Text, RelayTcp.Text }));
    private async Task RenderPreviewAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i; UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(folder, "tab-" + i + ".png")); encoder.Save(output);
        }
        File.WriteAllText(Path.Combine(folder, "complete.txt"), "Rendered " + Tabs.Items.Count + " tabs. No server, router mapping, or firewall action was started."); Close();
    }
    private void Guide_Click(object s, RoutedEventArgs e) => MessageBox.Show(this, "CREATE SESSION: Choose tracks and settings, then create the session. Direct hosting can request a router mapping. The integrated Capy room network uses a public relay backend running inside another copy of this same manager; paste its private host pairing once on Connect, then enable Capy room networking.\n\nJOIN SESSION: Friends paste the capyroom:// invite into this app and press Join session. The manager establishes its own encrypted player tunnel and connects the game to a local endpoint. Keep the manager open. No Radmin, VPN driver, standalone relay tool, or system routing changes are needed. Everyone still needs matching track files.\n\nPUBLIC BACKEND: Use Run relay inside the app on a publicly reachable PC/VPS. Allow its tunnel TCP port, or map it through the router. The optional UDP port supports direct native clients. A public backend is necessary for blocked/CGNAT hosts; this release does not provision a hosted relay service automatically.\n\nTRACKINFOS: Load a track in-game and chat /savetrackinfo. The app imports the export without fabricating hashes.", "In-app session network");
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; if (busy) { e.Cancel = true; Notice = "Wait for the current operation to finish."; return; }
        e.Cancel = true;
        if ((server.Running || relay != null || playerBridge is { Connected: true }) && MessageBox.Show(this, "Close hosted and joined sessions, then exit the manager?", "Close manager", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        closing = true;
        try { timer.Stop(); watcher?.Dispose(); await server.StopAsync(); await CleanupConnectionAsync(); if (playerBridge != null) await playerBridge.DisposeAsync(); await StopRelayAsync(); identity?.Dispose(); Save(); SaveRelaySettings(); _ = Dispatcher.BeginInvoke(new Action(Close)); }
        catch (Exception ex)
        {
            var args = Environment.GetCommandLineArgs(); var render = Array.IndexOf(args, "--render");
            if (render >= 0 && render + 1 < args.Length) { File.WriteAllText(Path.Combine(args[render + 1], "close-error.txt"), ex.ToString()); Application.Current.Shutdown(1); return; }
            closing = false; timer.Start(); WatchGame(); MessageBox.Show(this, ex.Message);
        }
    }
}
