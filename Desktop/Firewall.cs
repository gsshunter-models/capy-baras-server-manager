using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CapyServer.Desktop;
public static class Firewall
{
    private static string Rule(string protocol, int port) => $"Capy Bara Server Manager {protocol} {port}";
    public static bool IsConfigured(int udpPort, int? tcpPort = null)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        try
        {
            foreach (var (name, protocol, port) in new[] { ("UDP", 17, udpPort), ("TCP", 6, tcpPort ?? 0) }.Where(x => x.Item3 > 0))
            {
                dynamic? rule = null;
                try { rule = policy.Rules.Item(Rule(name, port)); if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1 || (int)rule.Protocol != protocol || (string)rule.LocalPorts != port.ToString() || (int)rule.Profiles != 7) return false; }
                catch (COMException) { return false; }
                finally { if (rule != null) Marshal.FinalReleaseComObject(rule); }
            }
            return true;
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }
    public static Task EnsureAsync(int udpPort, int? tcpPort = null) => IsConfigured(udpPort, tcpPort) ? Task.CompletedTask : ConfigureAsync(udpPort, tcpPort);
    public static async Task ConfigureAsync(int udpPort, int? tcpPort, bool remove = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--firewall"); start.ArgumentList.Add(remove ? "remove" : "add"); start.ArgumentList.Add(udpPort.ToString()); if (tcpPort != null) start.ArgumentList.Add(tcpPort.ToString()!);
        using var child = Process.Start(start) ?? throw new IOException("Could not start the connection helper."); await child.WaitForExitAsync(); if (child.ExitCode != 0) throw new IOException("Firewall setup was not completed.");
    }
    public static void Apply(string[] args)
    {
        var index = Array.IndexOf(args, "--firewall");
        if (index < 0 || index + 2 >= args.Length || args[index + 1] is not ("add" or "remove") || !int.TryParse(args[index + 2], out var udp) || udp is < 1024 or > 65535) throw new InvalidDataException("Invalid firewall request.");
        var tcp = 0; if (index + 3 < args.Length && (!int.TryParse(args[index + 3], out tcp) || tcp is < 1024 or > 65535)) throw new InvalidDataException("Invalid TCP port.");
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        try
        {
            foreach (var (name, protocol, port) in new[] { ("UDP", 17, udp), ("TCP", 6, tcp) }.Where(x => x.Item3 > 0))
            {
                var ruleName = Rule(name, port);
                if (args[index + 1] == "remove") { try { policy.Rules.Remove(ruleName); } catch (COMException) { } continue; }
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                try { rule.Name = ruleName; rule.Description = "Inbound connection for a server explicitly hosted with Capy Bara's Server Manager."; rule.Protocol = protocol; rule.LocalPorts = port.ToString(); rule.Direction = 1; rule.Action = 1; rule.Enabled = true; rule.Profiles = 7; rule.Grouping = "Capy Bara's Server Manager"; policy.Rules.Add(rule); } finally { Marshal.FinalReleaseComObject(rule); }
            }
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }
}
