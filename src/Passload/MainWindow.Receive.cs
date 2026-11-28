using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Passload.Core;

namespace Passload;

public partial class MainWindow
{
    private ReceiveClient? _receiver;
    private CancellationTokenSource? _receiveStop;
    private Task? _receiveTask;
    private Manifest? _incomingManifest;
    private string? _receiveMessageKey;

    private void ReceiveUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (IncomingCard is null || InspectButton is null) return;
        _incomingManifest = null;
        RefreshIncoming();
        _receiver?.Dispose();
        _receiver = null;
        InspectButton.IsEnabled = !string.IsNullOrWhiteSpace(ReceiveUrl.Text);
    }

    private async void InspectLink(object sender, RoutedEventArgs e)
    {
        if (_receiveTask is not null) return;
        if (InternetSharing.IsLegacyLink(ReceiveUrl.Text, out var legacyLink))
        {
            try
            {
                var page = await _internet.LegacyReaderUriAsync(legacyLink!, _lifetime.Token);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
                Notice(Locale.Text("LinkOpenedInBrowser"));
            }
            catch (Exception ex) { Notice(Friendly(ex), true); }
            return;
        }
        await ReceiveWork(async token =>
        {
            _incomingManifest = null;
            RefreshIncoming();
            _receiver?.Dispose();
            // A router may not loop connections to its public IP back to the originating PC.
            var address = ShareLinks.DirectAddress(ReceiveUrl.Text);
            var own = _sharing.Active.FirstOrDefault(s => s.Link.AbsoluteUri == address);
            var link = own is null ? address : new UriBuilder(own.Link)
                { Host = "127.0.0.1", Port = _sharing.LocalPort ?? _settings.Port }.Uri.AbsoluteUri;
            _receiver = new ReceiveClient(link);
            _incomingManifest = await _receiver.InspectAsync(token);
            RefreshIncoming();
            Notice(Locale.Text("SenderVerified", _incomingManifest.ExpiresAt.LocalDateTime.ToString("g", Locale.Current.Culture)));
        }, "ConnectingToSender");
    }

    private void RefreshIncoming()
    {
        var data = _incomingManifest;
        IncomingCard.Visibility = data is null ? Visibility.Collapsed : Visibility.Visible;
        if (data is null) return;
        IncomingSummary.Text = Display.Count(data.Files.Length);
        ZipButton.Visibility = data.Files.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        IncomingRows.Children.Clear();
        foreach (var file in data.Files)
            IncomingRows.Children.Add(MakeRow(file.Name, file.Size, Locale.Text("Download"), () => StartDownload(file)));
    }

    private void DownloadZip(object sender, RoutedEventArgs e) => StartDownload(null);

    private async void StartDownload(FileItem? item)
    {
        if (_receiveTask is not null || _receiver is null) return;
        var dialog = new SaveFileDialog
        {
            Title = Locale.Text("SaveDownload"),
            FileName = item is null ? "Passload.zip" : ReceiveClient.SuggestedName(item.Name),
            Filter = item is null ? Locale.Text("ZipArchive") + "|*.zip" : Locale.Text("AllFiles") + "|*.*",
            AddExtension = item is null, OverwritePrompt = true
        };
        if (!SelectFile(dialog)) return;
        var overwrite = System.IO.File.Exists(dialog.FileName);
        await ReceiveWork(async token =>
        {
            DownloadProgress.IsIndeterminate = item is null;
            var progress = new Progress<long>(received =>
            {
                DownloadText.Text = item is null ? Locale.Text("DownloadReceived", Display.Size(received)) : $"{Display.Size(received)} / {Display.Size(item.Size)}";
                if (item is { Size: > 0 }) DownloadProgress.Value = Math.Min(100, 100.0 * received / item.Size);
            });
            await _receiver.DownloadAsync(item, dialog.FileName, progress, token, overwrite);
            Notice(Locale.Text("DownloadSaved", dialog.FileName));
        }, "Downloading");
    }

    private async Task ReceiveWork(Func<CancellationToken, Task> work, string messageKey)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _receiveStop = stop;
        ReceiveUrl.IsEnabled = InspectButton.IsEnabled = IncomingCard.IsEnabled = false;
        _receiveMessageKey = messageKey;
        DownloadText.Text = Locale.Text(messageKey);
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Value = 0;
        NoticeBox.Visibility = Visibility.Collapsed;
        try
        {
            _receiveTask = work(stop.Token);
            await _receiveTask;
        }
        catch (Exception ex)
        {
            if (!_closing) Notice(stop.IsCancellationRequested ? Locale.Text("ReceivingCanceled") : Friendly(ex), !stop.IsCancellationRequested);
        }
        finally
        {
            _receiveMessageKey = null;
            _receiveTask = null;
            _receiveStop = null;
            DownloadPanel.Visibility = Visibility.Collapsed;
            ReceiveUrl.IsEnabled = InspectButton.IsEnabled = IncomingCard.IsEnabled = true;
        }
    }

    private void CancelReceive(object sender, RoutedEventArgs e) => _receiveStop?.Cancel();
}
