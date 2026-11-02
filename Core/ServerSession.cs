using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapyServer.Core;

public sealed class ServerSnapshot
{
    public string Track { get; set; } = "";
    public string Directory { get; set; } = "";
    public double Erode { get; set; }
    public int Laps { get; set; }
    public double Minutes { get; set; }
    public double RaceSeconds { get; set; }
    public List<PlayerSnapshot> Players { get; set; } = [];
}
public sealed class PlayerSnapshot
{
    public int Slot { get; set; }
    public int Uid { get; set; }
    public string Status { get; set; } = "";
    public string Rank { get; set; } = "";
    public int Ping { get; set; }
}
public sealed class ServerSession : IAsyncDisposable
{
    private Process? process;
    private OwnedProcessJob? job;
    private string secret = "";
    private int port;
    private IPAddress controlAddress = IPAddress.Loopback;
    public string SessionFolder { get; private set; } = "";
    public bool Running => process is { HasExited: false };
    public event Action<string>? Log;
    public event Action? Exited;
    public static string BuildArguments(ServerProfile profile, string profileRoot, string session, string secret, bool redact = false)
    {
        NativeArguments.Validate(profile); Directory.CreateDirectory(session);
        var lines = new List<string>();
        void Add(string option, object value) => lines.Add(option + " " + NativeArguments.Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        Add("--port", profile.Port); Add("--address", profile.BindAddress); Add("--max-clients", profile.MaxClients); Add("--max-players", profile.MaxPlayers); Add("--rate", profile.Rate); Add("--erode", profile.Erode);
        if (profile.AdminUid.Length > 0) Add("--admin", profile.AdminUid);
        if (profile.Greeting.Length > 0) Add("--greeting", profile.Greeting);
        Add("--xcmd-password", redact ? "[local control secret]" : secret);
        var fileOptions = new Dictionary<string, string> { ["--ban-file"] = Path.Combine(profileRoot, "bans.txt"), ["--results-file"] = Path.Combine(session, "results.txt"), ["--lineup-file"] = Path.Combine(session, "lineup.txt") };
        foreach (var option in profile.Options.Where(o => o.Enabled))
        {
            var spec = OptionCatalog.Specs.First(s => s.Flag == option.Flag);
            if (spec.Switch) { lines.Add(option.Flag); continue; }
            foreach (var item in spec.Repeat ? option.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [option.Value])
            {
                var value = item;
                if (option.Flag is "--ban-file" or "--lineup-file" or "--results-file" or "--demo-file" or "--sendfile-prefix")
                {
                    if (Path.IsPathRooted(value)) throw new InvalidDataException("Use a relative managed filename for " + option.Flag);
                    var root = option.Flag == "--ban-file" ? profileRoot : session;
                    value = Path.GetFullPath(value, root);
                    if (!value.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Output path escapes the profile.");
                    Directory.CreateDirectory(option.Flag == "--sendfile-prefix" ? value : Path.GetDirectoryName(value)!);
                    fileOptions.Remove(option.Flag);
                }
                if (option.Flag == "--javascript") { value = Path.GetFullPath(value, profile.ServerFolder); if (!File.Exists(value)) throw new FileNotFoundException("Server JavaScript not found.", value); }
                Add(option.Flag, redact && option.Flag == "--password" ? "[admin password]" : value);
            }
        }
        foreach (var entry in fileOptions) Add(entry.Key, entry.Value);
        var script = Path.Combine(session, "capy-status.js"); File.WriteAllText(script, StatusScript(Path.Combine(session, "status.json")), new UTF8Encoding(false)); Add("--javascript", script);
        var index = 0;
        foreach (var row in profile.Rotation.Where(r => r.Enabled))
        {
            var destination = Path.Combine(session, $"track-{index++:D3}.trackinfo"); File.Copy(row.Path, destination, true);
            Add("--minutes", row.Minutes); Add("--laps", row.Laps); Add("--track-interval", row.Interval);
            Add("--track", destination);
            if (row.NextTrack is int next) Add("--next-track", next);
        }
        return string.Join('\n', lines) + "\n";
    }
    public async Task StartAsync(ServerProfile profile, string state)
    {
        if (Running) throw new InvalidOperationException("Stop the current server first.");
        var exe = Path.GetFullPath(Path.Combine(profile.ServerFolder, "mxserver.exe"));
        if (!File.Exists(exe)) throw new FileNotFoundException("Choose the folder containing mxserver.exe. The native server is separate from this manager.", exe);
        NativeArguments.Validate(profile);
        port = profile.Port; var bind = IPAddress.Parse(profile.BindAddress); controlAddress = bind.Equals(IPAddress.Any) ? IPAddress.Loopback : bind;
        using (var probe = new UdpClient(new IPEndPoint(bind, port))) { }
        var root = ProfileStore.Root(state, profile); SessionFolder = Path.Combine(root, "sessions", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        secret = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "/";
        var args = BuildArguments(profile, root, SessionFolder, secret); var path = Path.Combine(SessionFolder, "serverargs.txt"); File.WriteAllText(path, args, new UTF8Encoding(false));
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--args-file"); start.ArgumentList.Add(path);
        var native = new Process { StartInfo = start, EnableRaisingEvents = true }; process = native;
        void Line(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return; var line = e.Data.Replace(secret, "[control secret]");
            foreach (var option in profile.Options.Where(o => o.Enabled && o.Flag == "--password" && o.Value.Length > 0)) line = line.Replace(option.Value, "[admin password]");
            if (line.Length > 2000) line = line[..2000]; Log?.Invoke(line);
        }
        native.OutputDataReceived += Line; native.ErrorDataReceived += Line; native.Exited += (_, _) => Exited?.Invoke();
        var started = false;
        try { if (!native.Start()) throw new IOException("Could not start mxserver."); started = true; job = OwnedProcessJob.Attach(native); native.BeginOutputReadLine(); native.BeginErrorReadLine(); await Task.Delay(450); if (native.HasExited) throw new IOException("mxserver exited during startup. Check the server log and settings."); }
        catch { job?.Dispose(); job = null; if (started && !native.HasExited) native.Kill(); native.Dispose(); process = null; throw; }
        Log?.Invoke("Native server started. Session files: " + SessionFolder);
    }
    public ServerSnapshot? Snapshot()
    {
        var path = Path.Combine(SessionFolder, "status.json");
        try { if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return null; return JsonSerializer.Deserialize<ServerSnapshot>(File.ReadAllText(path), ProfileStore.Json); } catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    public async Task CommandAsync(string command)
    {
        if (!Running) throw new InvalidOperationException("Start the server first.");
        if (string.IsNullOrWhiteSpace(command) || command.Length > 2000 || command.Any(char.IsControl)) throw new InvalidDataException("Use a single server command line.");
        if (command.StartsWith("server,")) command = command[7..].Trim();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(Encoding.UTF8.GetBytes("x" + secret + command), new IPEndPoint(controlAddress, port));
        Log?.Invoke("Local admin command sent: " + command);
    }
    public async Task StopAsync()
    {
        if (process is not { } native) return;
        try { if (!native.HasExited) { await CommandAsync("shutdown"); using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(6)); await native.WaitForExitAsync(wait.Token); } }
        catch (OperationCanceledException) { throw new IOException("Server has not acknowledged shutdown. Use Stop again or close its process manually; no unrelated process was terminated."); }
        finally { if (native.HasExited) { native.Dispose(); process = null; job?.Dispose(); job = null; } }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); }
    private static string StatusScript(string path) => "var capyPrevSecond = mxserver.second_handler;\nmxserver.second_handler = function() {\nif (capyPrevSecond) capyPrevSecond();\ntry { var p = []; for (var i = 0; i < mxserver.max_slots; i++) { var s = mxserver.get_status(i); if (s != 'Empty' && s != 'Nobody') p.push({Slot:i,Uid:mxserver.get_uid(i),Status:s,Rank:mxserver.get_rank(i),Ping:Math.round(mxserver.get_slot_info(i).ping * 1000)}); }\nvar state = {Track:mxserver.get_string('track_name',0),Directory:mxserver.get_string('track_dir',0),Erode:mxserver.get_number('erode',0)/1024,Laps:mxserver.get_number('finish_laps',0),Minutes:mxserver.get_number('finish_time',0)/7680,RaceSeconds:mxserver.get_number('race_time',0)/128,Players:p};\nmxserver.string_to_file(" + JsonSerializer.Serialize(path) + ", JSON.stringify(state));\n} catch(e) { mxserver.log('Capy status: ' + e); }\n};\n";
}
