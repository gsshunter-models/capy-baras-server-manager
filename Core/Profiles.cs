using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapyServer.Core;

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Friends' practice";
    public string ServerFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "mxserver");
    public string GameFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MX Simulator");
    public string GameExecutable { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MX Simulator", "mx.exe");
    public int Port { get; set; } = 19800;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Laps { get; set; } = 2;
    public int Minutes { get; set; } = 10;
    public int TrackInterval { get; set; } = 60;
    public double Erode { get; set; } = 1;
    public int MaxClients { get; set; } = 20;
    public int MaxPlayers { get; set; } = 20;
    public int Rate { get; set; } = 1000000;
    public string AdminUid { get; set; } = "";
    public string Greeting { get; set; } = "Welcome to the capybara paddock!";
    public bool AutoMap { get; set; } = true;
    public bool UseRelay { get; set; }
    public bool AutomaticSetup { get; set; } = true;
    public string DuckDnsDomain { get; set; } = "";
    public string RelayPairing { get; set; } = "";
    public ObservableCollection<RotationEntry> Rotation { get; set; } = [];
    public ObservableCollection<ServerOption> Options { get; set; } = OptionCatalog.NewOptions();
    public override string ToString() => Name;
}
public sealed class RotationEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public int Laps { get; set; } = 2;
    public int Minutes { get; set; } = 10;
    public int Interval { get; set; } = 60;
    public int? NextTrack { get; set; }
    public bool Enabled { get; set; } = true;
}
public sealed class ServerOption
{
    public string Flag { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Enabled { get; set; }
    public string Description => OptionCatalog.Specs.FirstOrDefault(s => s.Flag == Flag)?.Description ?? "Unsupported option";
}
public sealed record OptionSpec(string Flag, string Description, bool Switch = false, bool Repeat = false, bool Executable = false);
public static class OptionCatalog
{
    public static IReadOnlyList<OptionSpec> Specs { get; } = new[]
    {
        new OptionSpec("--ban-file", "Persistent bans file (manager supplies a default)"),
        new OptionSpec("--demo-file", "Record a native server demo to this file"),
        new OptionSpec("--extension", "Custom server command → shell command; one per line", Repeat:true, Executable:true),
        new OptionSpec("--inactivity-timeout", "Inactive client timeout in seconds (default 900)"),
        new OptionSpec("--javascript", "Load a server JavaScript file; one per line", Repeat:true, Executable:true),
        new OptionSpec("--lineup-file", "Live lineup output (manager supplies a default)"),
        new OptionSpec("--marshal", "Marshal UIDs, one per line", Repeat:true),
        new OptionSpec("--max-cuts", "Maximum track cuts before a rider becomes kickable"),
        new OptionSpec("--min-version", "Minimum allowed client protocol version"),
        new OptionSpec("--no-arcade", "Disallow arcade mode", Switch:true),
        new OptionSpec("--off-course-bias", "Off-course time bias, seconds (default 10)"),
        new OptionSpec("--off-course-scale", "Off-course leader-time multiplier (default 1.5)"),
        new OptionSpec("--password", "Administrator password, NOT a player join password"),
        new OptionSpec("--reserve", "UIDs allowed to use reserved slots, one per line", Repeat:true),
        new OptionSpec("--reserved-clients", "Number of reserved client slots (default 0)"),
        new OptionSpec("--results-cmd", "Execute this command after races", Executable:true),
        new OptionSpec("--results-file", "Race results output (manager supplies a default)"),
        new OptionSpec("--sendfile-prefix", "Prefix for native admin file uploads"),
        new OptionSpec("--max-updates", "Native maximum update limit"),
        new OptionSpec("--fake-lag", "Native network testing delay"),
        new OptionSpec("--fake-loss", "Native network testing packet loss"),
        new OptionSpec("--redirect", "Native redirect destination")
    };
    public static ObservableCollection<ServerOption> NewOptions() => new(Specs.Select(s => new ServerOption { Flag = s.Flag }));
}
public static class ProfileStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Root(string state, ServerProfile profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _)) throw new InvalidDataException("Invalid profile ID.");
        return Path.Combine(Path.GetFullPath(state), "servers", profile.Id);
    }
    public static List<ServerProfile> Load(string state)
    {
        var path = Path.Combine(state, "profiles.json");
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Profile file is too large.");
        return JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(path), Json) ?? [];
    }
    public static void Save(string state, IEnumerable<ServerProfile> profiles)
    {
        Directory.CreateDirectory(state); var path = Path.Combine(state, "profiles.json");
        var temp = path + ".new"; File.WriteAllText(temp, JsonSerializer.Serialize(profiles, Json)); File.Move(temp, path, true);
    }
    public static ServerProfile Copy(ServerProfile profile)
    { var copy = JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(profile, Json), Json)!; copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy"; return copy; }
}
public sealed record TrackInfo(string Path, string Directory, string Name, int Gates, string Hash)
{
    public override string ToString() => Name;
    public static TrackInfo Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 256 * 1024 || file.LinkTarget != null) throw new InvalidDataException("Trackinfo is missing, linked, or too large.");
        var values = File.ReadAllLines(path).Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());
        string Field(string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing trackinfo field: " + key);
        var dir = Field("dir");
        if (string.IsNullOrWhiteSpace(dir) || dir.Contains(':') || dir.StartsWith('/') || dir.StartsWith('\\') || dir.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "") || dir.Any(char.IsControl)) throw new InvalidDataException("Invalid track directory.");
        var name = Field("longname"); var hash = Field("hash");
        if (name.Length > 1000 || name.Any(char.IsControl) || hash.Length != 40 || !hash.All(Uri.IsHexDigit) || !int.TryParse(Field("gatecount"), out var gates) || gates is < 1 or > 1000) throw new InvalidDataException("Invalid trackinfo fields.");
        foreach (var key in new[] { "firstlap", "normallap" }) if (!int.TryParse(Field(key), out var n) || n < 1) throw new InvalidDataException("Invalid timing gates.");
        return new(System.IO.Path.GetFullPath(path), dir, name, gates, hash);
    }
    public static List<TrackInfo> Scan(params string[] roots)
    {
        var result = new List<TrackInfo>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!System.IO.Directory.Exists(root)) continue;
            try { foreach (var path in System.IO.Directory.EnumerateFiles(root, "*.trackinfo", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Take(20000)) { try { result.Add(Read(path)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { } } } catch (IOException) { }
        }
        return result.DistinctBy(t => t.Directory + "|" + t.Hash).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static TrackInfo Import(string source, string state)
    {
        _ = Read(source); var bytes = File.ReadAllBytes(source); var key = Convert.ToHexString(SHA256.HashData(bytes));
        var folder = System.IO.Path.Combine(state, "trackinfos"); System.IO.Directory.CreateDirectory(folder); var path = System.IO.Path.Combine(folder, key + ".trackinfo");
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes); return Read(path);
    }
}
public static class NativeArguments
{
    public static string Quote(string value)
    {
        if (value.Contains('"') || value.Any(char.IsControl)) throw new InvalidDataException("Arguments cannot contain quotes or control characters.");
        return "\"" + value + "\"";
    }
    public static List<string> Tokens(string text)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Argument file is too large.");
        var result = new List<string>(); var word = new StringBuilder(); var quoted = false; var started = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; started = true; }
            else if (!quoted && char.IsWhiteSpace(c)) { if (started) { result.Add(word.ToString()); word.Clear(); started = false; } }
            else { if (char.IsControl(c) && !char.IsWhiteSpace(c)) throw new InvalidDataException("Invalid argument character."); word.Append(c); started = true; }
        }
        if (quoted) throw new InvalidDataException("Unclosed quote in argument file.");
        if (started) result.Add(word.ToString()); return result;
    }
    public static ServerProfile Import(string path, ServerProfile basis)
    {
        var p = ProfileStore.Copy(basis); p.Name = System.IO.Path.GetFileNameWithoutExtension(path); p.Rotation.Clear(); p.Options = OptionCatalog.NewOptions();
        var args = Tokens(File.ReadAllText(path)); var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        int Number(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i]; string Value() => ++i < args.Count ? args[i] : throw new InvalidDataException("Missing value for " + flag);
            if (!flag.StartsWith("--") || flag == "--track")
            {
                var reference = flag == "--track" ? Value() : flag;
                var resolved = System.IO.Path.GetFullPath(reference, folder);
                if (!File.Exists(resolved)) resolved = System.IO.Path.GetFullPath(reference, p.ServerFolder);
                var info = TrackInfo.Read(resolved);
                p.Rotation.Add(new() { Path = info.Path, Name = info.Name, Laps = p.Laps, Minutes = p.Minutes, Interval = p.TrackInterval }); continue;
            }
            switch (flag)
            {
                case "--port": p.Port = Number(Value()); break;
                case "--address": p.BindAddress = Value(); break;
                case "--laps": p.Laps = Number(Value()); break;
                case "--minutes": p.Minutes = Number(Value()); break;
                case "--track-interval": p.TrackInterval = Number(Value()); break;
                case "--next-track": var next = Number(Value()); if (p.Rotation.Count > 0) p.Rotation[^1].NextTrack = next; break;
                case "--erode": p.Erode = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--max-clients": p.MaxClients = Number(Value()); break;
                case "--max-players": p.MaxPlayers = Number(Value()); break;
                case "--rate": p.Rate = Number(Value()); break;
                case "--admin": p.AdminUid = Value(); break;
                case "--greeting": p.Greeting = Value(); break;
                case "--xcmd-password": _ = Value(); break; // Always generate a new, local control secret.
                case "--args-file": throw new InvalidDataException("Import the referenced args file directly; nested argument files are not followed.");
                default:
                    var spec = OptionCatalog.Specs.FirstOrDefault(s => s.Flag == flag) ?? throw new InvalidDataException("Unsupported server option: " + flag);
                    var option = p.Options.First(o => o.Flag == flag); var value = spec.Switch ? "" : Value();
                    option.Value = spec.Repeat && option.Value.Length > 0 ? option.Value + "\n" + value : value; option.Enabled = true; break;
            }
        }
        return p;
    }
    public static void Validate(ServerProfile p)
    {
        if (p.Port is < 1024 or > 65535 || p.MaxClients is < 1 or > 160 || p.MaxPlayers is < 1 or > 160 || p.MaxPlayers > p.MaxClients || p.Rate is < 1024 or > 1000000000) throw new InvalidDataException("Use a port from 1024–65535, 1–160 clients, players ≤ clients, and a valid bandwidth limit.");
        if (!double.IsFinite(p.Erode) || p.Erode is < 0 or > 100) throw new InvalidDataException("Erode must be between 0 and 100.");
        if (!System.Net.IPAddress.TryParse(p.BindAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new InvalidDataException("Bind address must be an IPv4 address.");
        if (p.AdminUid.Length > 0 && (!int.TryParse(p.AdminUid, out var uid) || uid < 0)) throw new InvalidDataException("Admin UID must be a positive number or empty.");
        _ = Quote(p.Greeting);
        foreach (var row in p.Rotation.Where(t => t.Enabled))
        { if (row.Laps is < 0 or > 10000 || row.Minutes is < 0 or > 1440 || (row.Laps == 0 && row.Minutes == 0) || row.Interval is < -1 or > 10080) throw new InvalidDataException("Each track needs laps or minutes, and a rotation interval from -1 to 10080."); if (row.NextTrack is int next && (next < -160 || next >= p.Rotation.Count(r => r.Enabled))) throw new InvalidDataException("Next track must be a valid zero-based track number or a negative backward offset."); _ = TrackInfo.Read(row.Path); }
        if (!p.Rotation.Any(t => t.Enabled)) throw new InvalidDataException("Add at least one enabled track to the rotation.");
        foreach (var o in p.Options.Where(o => o.Enabled))
        {
            var spec = OptionCatalog.Specs.FirstOrDefault(s => s.Flag == o.Flag) ?? throw new InvalidDataException("Unknown option: " + o.Flag);
            if (!spec.Switch) { if (string.IsNullOrWhiteSpace(o.Value)) throw new InvalidDataException("Value required for " + o.Flag); foreach (var value in spec.Repeat ? o.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [o.Value]) { _ = Quote(value); } }
        }
    }
}
