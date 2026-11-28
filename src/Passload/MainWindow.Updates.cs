using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Passload.Core;

namespace Passload;

public partial class MainWindow
{
    private DateTimeOffset _nextUpdateCheck = DateTimeOffset.MaxValue;
    private UpdatePackage? _pendingUpdate;
    private bool _checkingUpdate, _installingUpdate;
    private bool HasDraft => _draft.Count != 0 || _draftDetails is { Title: not null } or { Description: not null };

    internal void StartUpdateChecks(string? startup)
    {
        if (WindowsPackage.HasIdentity)
        {
            CheckUpdateButton.SetResourceReference(ContentControl.ContentProperty, "Text.StoreUpdates");
            return;
        }
        CheckUpdateButton.IsEnabled = AppUpdate.Available;
        if (!AppUpdate.Available) return;
        AppUpdate.CleanOldDownloads();
        if (startup == "--update-incomplete")
        {
            _nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(1);
            Notice(Locale.Text("UpdateReplaceFailed"), true);
            return;
        }
        if (startup == "--updated") Notice(Locale.Text("UpdateInstalled", AppUpdate.Version.ToString(3)));
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
            if (manual) Notice(Locale.Text("UpdateReady"));
            return;
        }

        _checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        if (manual) Notice(Locale.Text("CheckingUpdates"));
        try
        {
            var update = await AppUpdate.CheckAsync(_lifetime.Token);
            if (_closing)
            {
                if (update is not null) AppUpdate.Delete(update.File);
                return;
            }
            _pendingUpdate = update;
            if (update is not null && (HasWork || HasDraft))
                Notice(Locale.Text("UpdateWaiting", update.Version.ToString(3)));
            else if (manual && update is null) Notice(Locale.Text("UpToDate", AppUpdate.Version.ToString(3)));
        }
        catch (Exception ex)
        {
            if (!_closing && manual) Notice(Locale.Text("UpdateCheckFailed") + " " + ex.Message, true);
        }
        finally
        {
            _checkingUpdate = false;
            if (!_closing) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task ApplyPendingUpdateAsync()
    {
        if (_closing || _installingUpdate || !IsEnabled || _dialogOpen || HasWork || HasDraft || _pendingUpdate is not { } update) return;
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
