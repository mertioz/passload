using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Passload.Core;

namespace Passload;

internal sealed record Settings(int Port, string Manual)
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Passload", "settings.json");
    public static Settings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            if (value is { Port: >= 1024 and <= 65535, Manual: not null })
            {
                NetworkInfo.ManualOrigin(value.Manual, value.Port);
                return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
        return new(NetworkInfo.DefaultPort, "");
    }

    public void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, FilePath, true); }
        finally { File.Delete(temporary); }
    }
}

public partial class MainWindow
{
    private Uri? _routerPage;
    private void RefreshNetwork(object sender, RoutedEventArgs e) => DisplayNetwork();

    private void DisplayNetwork()
    {
        try
        {
            var info = NetworkInfo.Read();
            LocalAddressText.Text = info.LanAddress is { } lan ? $"Destination de la redirection     {lan}  ·  TCP {_sharing.LocalPort ?? _settings.Port}" : "Adresse locale indisponible.";
            IPv6Text.Text = info.PublicV6 is null ? "IPv6 publique non détectée sur ce PC." : $"IPv6 publique détectée     {info.PublicV6}";
            var gateway = info.Gateways.FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g));
            _routerPage = gateway is null ? null : new UriBuilder("http", gateway.ToString()).Uri;
            RouterButton.IsEnabled = _routerPage is not null;
        }
        catch (Exception ex) { Notice(Friendly(ex), true); }
    }

    private void OpenRouter(object sender, RoutedEventArgs e)
    {
        if (_routerPage is null) return;
        try { Process.Start(new ProcessStartInfo(_routerPage.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Notice(Friendly(ex), true); }
    }

    private Settings ReadSettings()
    {
        if (!int.TryParse(PortInput.Text, out var port) || port is < 1024 or > 65535)
            throw new FormatException("Choisissez un port entre 1024 et 65535.");
        var manual = ManualInput.Text.Trim();
        NetworkInfo.ManualOrigin(manual, port);
        var next = new Settings(port, manual);
        if ((_sharing.Active.Length > 0 || _creating) && next != _settings)
            throw new InvalidOperationException("Arrêtez les partages en cours avant de modifier la connexion.");
        return next;
    }

    private void SaveNetwork(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = ReadSettings();
            next.Write();
            _settings = next;
            DisplayNetwork();
            Notice("Réglages de connexion enregistrés.");
        }
        catch (Exception ex) { Notice(Friendly(ex), true); }
    }

    private async void RequestFirewall(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var next = ReadSettings();
            if (next != _settings) throw new InvalidOperationException("Enregistrez les réglages avant d’autoriser le port dans Windows.");
            var port = _sharing.LocalPort ?? next.Port;
            await Firewall.RequestAsync(port);
            Notice($"Windows autorise Passload à recevoir des connexions sur le port TCP {port}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Notice("Demande de pare-feu annulée."); }
        catch (Exception ex) { Notice(Friendly(ex), true); }
        finally { button.IsEnabled = true; }
    }

    private void ShowLicenses(object sender, RoutedEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var sections = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".txt") || n.EndsWith(".LICENSE")))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            sections.Add(reader.ReadToEnd().Trim());
        }
        var content = new TextBox { Text = string.Join("\n\n──────────────\n\n", sections), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(15), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Top };
        new Window { Owner = this, Title = "Passload · Licences", Width = 670, Height = 490, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content }.ShowDialog();
    }
}
