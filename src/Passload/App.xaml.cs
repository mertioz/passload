using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Passload;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _activation;
    private bool _owner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        if (e.Args is ["--install-update", var previous, var hash, var presentation] && int.TryParse(previous, out var previousId))
        {
            Shutdown(AppUpdate.Install(previousId, hash, presentation == "show"));
            return;
        }
        if (e.Args is ["--authorize-port", var value] && int.TryParse(value, out var port) && port is >= 1024 and <= 65535)
        {
            Shutdown(Firewall.Authorize(port));
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var scope = @"Local\Passload-Tls-" + (identity.User?.Value ?? Environment.UserName);
        _showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, scope + "-show");
        _instance = new Mutex(false, scope + "-instance");
        try { _owner = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _owner = true; }
        if (!_owner)
        {
            _showWindow.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        _activation = ThreadPool.RegisterWaitForSingleObject(_showWindow,
            (_, _) => Dispatcher.BeginInvoke(new Action(window.Reopen)), null, Timeout.Infinite, false);
        window.Show();
        window.StartUpdateChecks(e.Args.Length == 1 ? e.Args[0] : null);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel && MainWindow is MainWindow window) window.PrepareForSystemExit();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Unregister(null);
        _showWindow?.Dispose();
        if (_owner) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

internal static class Firewall
{
    private const int Tcp = 6;
    private const int Inbound = 1;
    private const int Allow = 1;
    private static readonly int[] Profiles = [1, 2, 4];

    public static int Authorize(int port)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            var executable = Environment.ProcessPath!;
            var installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())).AsSpan(0, 5));
            rule.Name = $"Passload TCP {port} ({installation})";
            rule.Description = "Téléchargements HTTPS depuis Passload.";
            rule.ApplicationName = executable;
            rule.Protocol = Tcp;
            rule.LocalPorts = port.ToString(CultureInfo.InvariantCulture);
            rule.Direction = Inbound;
            rule.Action = Allow;
            rule.Profiles = int.MaxValue;
            rule.Enabled = true;
            policy.Rules.Add(rule);
            return 0;
        }
        catch { return 1; }
    }

    public static async Task EnsureInboundAsync(int port, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!NeedsAuthorization(port)) return;
        try { await RequestAsync(port, cancellation); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows attend votre autorisation pour le partage par adresse IP.", ex);
        }
    }

    private static bool NeedsAuthorization(int port)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
            int activeProfiles = policy.CurrentProfileTypes;
            int blockedByDefault = 0;
            foreach (var profile in Profiles)
                if ((activeProfiles & profile) != 0 && policy.FirewallEnabled[profile] && policy.DefaultInboundAction[profile] != Allow)
                    blockedByDefault |= profile;
            if (blockedByDefault == 0) return false;
            foreach (dynamic rule in policy.Rules)
            {
                if (!rule.Enabled || rule.Direction != Inbound || rule.Action != Allow || rule.Protocol is not (Tcp or 256) ||
                    rule.RemoteAddresses != "*" || rule.LocalAddresses != "*" ||
                    rule.ServiceName is string service && service.Length != 0) continue;
                if (rule.ApplicationName is string app && app.Length != 0 &&
                    !string.Equals(Environment.ExpandEnvironmentVariables(app), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (rule.Protocol == Tcp && (rule.RemotePorts != "*" || !IncludesPort((string?)rule.LocalPorts, port))) continue;
                blockedByDefault &= ~(int)rule.Profiles;
                if (blockedByDefault == 0) return false;
            }
            return true;
        }
        catch { return false; } // Windows can still display its own prompt or apply an administrator's rule.
    }

    private static bool IncludesPort(string? ports, int port)
    {
        if (ports == "*") return true;
        return ports?.Split(',').Any(range =>
        {
            var ends = range.Split('-', 2);
            if (!int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)) return false;
            return ends.Length == 1 ? port == first :
                int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last) && first <= port && port <= last;
        }) == true;
    }

    public static async Task RequestAsync(int port, CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--authorize-port");
        info.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows n’a pas ouvert la demande.");
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows n’a pas pu enregistrer la règle de pare-feu.");
    }
}
