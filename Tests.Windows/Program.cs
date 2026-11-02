using CapyServer.Core;
using CapyServer.Desktop;
using System.IO;
using System.Text;

if (args.Contains("--firewall-diagnostic"))
{
    try { Console.WriteLine("Firewall rule configured: " + Firewall.IsConfigured(63991)); return 0; }
    catch (Exception ex) { Console.WriteLine($"{ex.GetType().FullName} · 0x{ex.HResult:X8} · {ex.Message}"); return 1; }
}

var root = Path.Combine(Path.GetTempPath(), "CapySecretTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    if (Firewall.IsConfigured(63991)) throw new Exception("The diagnostic port unexpectedly already has a configured rule; choose an unused port for this check.");
    Console.WriteLine("PASS Missing Windows firewall rule returns not configured without changing firewall settings.");
    const string token = "synthetic-private-duckdns-token"; var profile = new ServerProfile(); SecretStore.Save(root, profile, token);
    var bytes = File.ReadAllBytes(SecretStore.PathFor(root, profile));
    if (Encoding.UTF8.GetString(bytes).Contains(token) || SecretStore.Read(root, profile) != token) throw new Exception("Encrypted credential roundtrip failed.");
    var other = new ServerProfile(); File.Copy(SecretStore.PathFor(root, profile), SecretStore.PathFor(root, other));
    var denied = false; try { _ = SecretStore.Read(root, other); } catch (IOException) { denied = true; }
    if (!denied) throw new Exception("A credential was accepted under a different profile identity.");
    ProfileStore.Save(root, new[] { profile }); if (File.ReadAllText(Path.Combine(root, "profiles.json")).Contains(token)) throw new Exception("Credential leaked into profile JSON.");
    Console.WriteLine("PASS Windows encrypted token roundtrip, profile isolation and JSON exclusion."); return 0;
}
catch (Exception ex) { Console.WriteLine(ex); return 1; }
finally
{
    var full = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("CapySecretTest-")) throw new Exception("Unsafe test path.");
    Directory.Delete(full, true);
}
