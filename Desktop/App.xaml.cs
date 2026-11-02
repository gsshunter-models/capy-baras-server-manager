using System.Windows;
namespace CapyServer.Desktop;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--firewall")) { try { Firewall.Apply(e.Args); Shutdown(0); } catch (Exception ex) { MessageBox.Show(ex.Message, "Connection setup"); Shutdown(1); } return; }
        var index = Array.IndexOf(e.Args, "--state"); var identity = index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : Environment.UserName;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..20];
        instance = new Mutex(true, "Local\\CapyServerManager-" + hash, out var created);
        if (!created) { MessageBox.Show("Capy Bara's Server Manager is already open."); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true; var render = Array.IndexOf(e.Args, "--render");
            if (render >= 0 && render + 1 < e.Args.Length) { System.IO.Directory.CreateDirectory(e.Args[render + 1]); System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[render + 1], "unhandled-error.txt"), args.Exception.ToString()); Shutdown(1); return; }
            MessageBox.Show(args.Exception.Message, "Capy Bara's Server Manager");
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
