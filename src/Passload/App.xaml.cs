using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace Passload;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        if (e.Args is ["--authorize-port", var value] && int.TryParse(value, out var port) && port is >= 1024 and <= 65535)
        {
            Shutdown(Firewall.Authorize(port));
            return;
        }
        new MainWindow().Show();
    }
}

internal static class Firewall
{
    public static int Authorize(int port)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            rule.Name = "Passload - partage direct (TCP)";
            rule.Description = "Téléchargements HTTPS depuis Passload.";
            rule.ApplicationName = Environment.ProcessPath!;
            rule.Protocol = 6;
            rule.LocalPorts = port.ToString(CultureInfo.InvariantCulture);
            rule.Direction = 1;
            rule.Action = 1;
            rule.Profiles = int.MaxValue;
            rule.Enabled = true;
            policy.Rules.Add(rule);
            return 0;
        }
        catch { return 1; }
    }

    public static async Task RequestAsync(int port)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--authorize-port");
        info.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows n’a pas ouvert la demande.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows n’a pas pu enregistrer la règle de pare-feu.");
    }
}
