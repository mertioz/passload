using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace Passload;

public partial class MainWindow
{
    private DateTimeOffset _nextUpdateCheck = DateTimeOffset.MaxValue;
    private UpdatePackage? _pendingUpdate;
    private bool _checkingUpdate, _installingUpdate;

    internal void StartUpdateChecks(string? startup)
    {
        if (WindowsPackage.HasIdentity)
        {
            CheckUpdateButton.Content = "Mises à jour du Microsoft Store";
            return;
        }
        CheckUpdateButton.IsEnabled = AppUpdate.Available;
        if (!AppUpdate.Available) return;
        AppUpdate.CleanOldDownloads();
        if (startup == "--update-incomplete")
        {
            _nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(1);
            Notice("La mise à jour n’a pas pu remplacer l’exécutable.", true);
            return;
        }
        if (startup == "--updated") Notice($"Passload {AppUpdate.Version.ToString(3)} est installé.");
        _ = CheckUpdatesAsync(manual: false);
    }

    private async void CheckUpdate(object sender, RoutedEventArgs e)
    {
        if (!WindowsPackage.HasIdentity)
        {
            await CheckUpdatesAsync(manual: true);
            return;
        }
        try { Process.Start(new ProcessStartInfo("ms-windows-store://downloadsandupdates") { UseShellExecute = true }); }
        catch (Win32Exception ex) { Notice(Friendly(ex), true); }
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_closing || _checkingUpdate || !AppUpdate.Available) return;
        _nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(1);
        if (_pendingUpdate is not null)
        {
            if (manual) Notice("Mise à jour prête. Elle s’installera dès que Passload sera libre.");
            return;
        }

        _checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        if (manual) Notice("Vérification des mises à jour…");
        try
        {
            var update = await AppUpdate.CheckAsync(_lifetime.Token);
            if (_closing)
            {
                if (update is not null) AppUpdate.Delete(update.File);
                return;
            }
            _pendingUpdate = update;
            if (update is not null && (HasWork || _draft.Count != 0))
                Notice($"Passload {update.Version.ToString(3)} est prêt à s’installer après les opérations en cours.");
            else if (manual && update is null) Notice($"Passload {AppUpdate.Version.ToString(3)} est à jour.");
        }
        catch (Exception ex)
        {
            if (!_closing && manual) Notice("Vérification des mises à jour impossible. " + ex.Message, true);
        }
        finally
        {
            _checkingUpdate = false;
            if (!_closing) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task ApplyPendingUpdateAsync()
    {
        if (_closing || _installingUpdate || !IsEnabled || _choosingFile || HasWork || _draft.Count != 0 || _pendingUpdate is not { } update) return;
        _installingUpdate = true;
        IsEnabled = false;
        try
        {
            await AppUpdate.StartInstallerAsync(update, reopen: IsVisible, _lifetime.Token);
            await QuitAsync();
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            AppUpdate.Delete(update.File);
            if (!_closing) Notice(ex.Message, true);
        }
        finally
        {
            _installingUpdate = false;
            if (!_closing) IsEnabled = true;
        }
    }
}
