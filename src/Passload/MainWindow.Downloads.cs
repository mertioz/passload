using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Passload.Core;

namespace Passload;

public partial class MainWindow
{
    private readonly Dictionary<Guid, Button> _downloadCounters = new();
    private long _downloadRevision = -1;

    private void RefreshDownloadCounters(bool force = false)
    {
        var revision = _history.Revision;
        if (!force && revision == _downloadRevision) return;
        _downloadRevision = revision;
        if (_downloadCounters.Count == 0) return;
        var counts = _history.Recent().ToLookup(record => record.ShareId);
        foreach (var (id, button) in _downloadCounters)
        {
            var files = counts[id];
            var text = CountDownloads(files);
            button.Content = text;
            AutomationProperties.SetName(button, "Détails des téléchargements · " + text);
        }
    }

    private void OpenDownloadHistory(object sender, RoutedEventArgs e) => ShowDownloads(null);

    private void ShowDownloads(Share? share)
    {
        var header = new TextBlock
        {
            Text = share?.Name ?? "Historique des téléchargements",
            FontSize = 27, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
        };
        var counts = new TextBlock
        {
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 7, 0, 14)
        };
        AutomationProperties.SetAutomationId(counts, "DownloadSummary");
        var note = new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 13, 0, 0)
        };
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserResizeRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Background = Brushes.White,
            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            FontSize = 14, RowHeight = 32, ColumnHeaderHeight = 31
        };
        AutomationProperties.SetAutomationId(grid, "DownloadEntries");
        if (share is null) grid.Columns.Add(Column("Partage", nameof(DownloadRow.Share), 1));
        grid.Columns.Add(Column("Fichier", nameof(DownloadRow.File), 1.2));
        grid.Columns.Add(Column("Date", nameof(DownloadRow.Date), 111, fixedWidth: true));
        grid.Columns.Add(Column("Adresse IP", nameof(DownloadRow.Ip), 1.15));
        grid.Columns.Add(Column("Transfert", nameof(DownloadRow.Mode), 103, fixedWidth: true));
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(counts);
        var panel = new DockPanel { Margin = new Thickness(21) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(note);
        panel.Children.Add(grid);
        var window = new Window
        {
            Title = "Téléchargements", Owner = this, Content = panel,
            Width = share is null ? 842 : 686, Height = 485, MinWidth = 540, MinHeight = 315,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Brushes.White, Foreground = (Brush)FindResource("Ink"), FontFamily = FontFamily
        };

        long last = -1;
        void Refresh()
        {
            var revision = _history.Revision;
            if (revision == last) return;
            last = revision;
            var entries = _history.Recent();
            if (share is not null) entries = entries.Where(item => item.ShareId == share.HistoryId).ToArray();
            counts.Text = CountDownloads(entries);
            grid.ItemsSource = entries.Select(item => new DownloadRow(item.ShareName, item.FileName,
                item.At.LocalDateTime.ToString("dd/MM HH:mm:ss"), item.Address ?? "Indisponible", item.Transport switch
                {
                    DownloadTransport.WebRtc => "WebRTC",
                    DownloadTransport.EncryptedRelay => "Relais chiffré",
                    _ => "HTTPS"
                })).ToArray();
            note.Text = "Une adresse IP peut être partagée par plusieurs personnes. Journal local des 30 derniers jours, limité aux 5 000 derniers transferts.";
            if (_history.PersistenceUnavailable) note.Text += " Le journal est temporairement en mémoire.";
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        window.Closed += (_, _) => timer.Stop();
        Refresh();
        timer.Start();
        window.ShowDialog();
    }

    private static DataGridTextColumn Column(string title, string property, double width, bool fixedWidth = false) => new()
    {
        Header = title, Binding = new Binding(property),
        Width = new DataGridLength(width, fixedWidth ? DataGridLengthUnitType.Pixel : DataGridLengthUnitType.Star)
    };

    private static string CountDownloads(IEnumerable<DownloadRecord> source)
    {
        var records = source.ToArray();
        var unique = records.Where(item => item.Address is not null).Select(item => item.Address).Distinct().Count();
        var count = records.Length == 1 ? "1 téléchargement" : $"{records.Length} téléchargements";
        if (unique != 0) count += $" · {unique} IP distincte{(unique > 1 ? "s" : "")}";
        var unknown = records.Count(item => item.Address is null);
        if (unknown != 0) count += $" · {unknown} sans IP";
        return count;
    }

    private sealed record DownloadRow(string Share, string File, string Date, string Ip, string Mode);
}
