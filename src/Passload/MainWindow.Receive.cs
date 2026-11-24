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

    private void ReceiveUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (IncomingCard is null || InspectButton is null) return;
        IncomingCard.Visibility = Visibility.Collapsed;
        _receiver?.Dispose();
        _receiver = null;
        InspectButton.IsEnabled = !string.IsNullOrWhiteSpace(ReceiveUrl.Text);
    }

    private async void InspectLink(object sender, RoutedEventArgs e)
    {
        if (_receiveTask is not null) return;
        await ReceiveWork(async token =>
        {
            IncomingCard.Visibility = Visibility.Collapsed;
            _receiver?.Dispose();
            _receiver = new ReceiveClient(ReceiveUrl.Text);
            var data = await _receiver.InspectAsync(token);
            IncomingSummary.Text = Display.Count(data.Files.Length);
            ZipButton.Visibility = data.Files.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            IncomingRows.Children.Clear();
            foreach (var file in data.Files) IncomingRows.Children.Add(MakeRow(file.Name, file.Size, "Télécharger", () => StartDownload(file)));
            IncomingCard.Visibility = Visibility.Visible;
            Notice($"PC expéditeur vérifié. Lien valide jusqu’au {data.ExpiresAt.LocalDateTime:dd/MM à HH:mm}.");
        }, "Connexion au PC expéditeur…");
    }

    private void DownloadZip(object sender, RoutedEventArgs e) => StartDownload(null);

    private async void StartDownload(FileItem? item)
    {
        if (_receiveTask is not null || _receiver is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer le téléchargement",
            FileName = item is null ? "Passload.zip" : ReceiveClient.SuggestedName(item.Name),
            Filter = item is null ? "Archive ZIP (*.zip)|*.zip" : "Tous les fichiers (*.*)|*.*",
            AddExtension = item is null, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var overwrite = System.IO.File.Exists(dialog.FileName);
        await ReceiveWork(async token =>
        {
            DownloadProgress.IsIndeterminate = item is null;
            var progress = new Progress<long>(received =>
            {
                DownloadText.Text = item is null ? $"{Display.Size(received)} reçus" : $"{Display.Size(received)} / {Display.Size(item.Size)}";
                if (item is { Size: > 0 }) DownloadProgress.Value = Math.Min(100, 100.0 * received / item.Size);
            });
            await _receiver.DownloadAsync(item, dialog.FileName, progress, token, overwrite);
            Notice("Téléchargement enregistré dans " + dialog.FileName);
        }, "Téléchargement en cours…");
    }

    private async Task ReceiveWork(Func<CancellationToken, Task> work, string message)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _receiveStop = stop;
        ReceiveUrl.IsEnabled = InspectButton.IsEnabled = IncomingCard.IsEnabled = false;
        DownloadText.Text = message;
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
            if (!_closing) Notice(stop.IsCancellationRequested ? "Réception annulée." : Friendly(ex), !stop.IsCancellationRequested);
        }
        finally
        {
            _receiveTask = null;
            _receiveStop = null;
            DownloadPanel.Visibility = Visibility.Collapsed;
            ReceiveUrl.IsEnabled = InspectButton.IsEnabled = IncomingCard.IsEnabled = true;
        }
    }

    private void CancelReceive(object sender, RoutedEventArgs e) => _receiveStop?.Cancel();
}
