using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Passload.Core;

namespace Passload;

public partial class MainWindow : Window
{
    private readonly DownloadHistory _history;
    private readonly ShareService _sharing;
    private readonly InternetSharing _internet;
    private readonly ShareTray _tray;
    private Task<Share>? _createTask;
    private readonly Dictionary<string, FileInfo> _draft = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _lifetime = new();
    private Settings _settings = Settings.Load();
    private string? _lastLinks;
    private bool _creating, _closing, _readyToClose, _maintaining;
    private static readonly System.Windows.Media.Brush Good = new SolidColorBrush(Color.FromRgb(241, 243, 240));
    private static readonly System.Windows.Media.Brush Alert = new SolidColorBrush(Color.FromRgb(253, 241, 229));

    public MainWindow() : this(new DownloadHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Passload", "download-history.jsonl"))) { }

    internal MainWindow(DownloadHistory history)
    {
        _history = history;
        _sharing = new ShareService(history);
        InitializeComponent();
        _internet = new InternetSharing(Surface, _sharing, history);
        _tray = new ShareTray(Reopen, ExitFromTray);
        SendNav.IsChecked = true;
        PortInput.Text = _settings.Port.ToString();
        ManualInput.Text = _settings.Manual;
        RefreshLinks();
        InspectButton.IsEnabled = false;
        _timer.Tick += Tick;
        _timer.Start();
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || SendPage is null) return;
        SendPage.Visibility = tag == "Send" ? Visibility.Visible : Visibility.Collapsed;
        ReceivePage.Visibility = tag == "Receive" ? Visibility.Visible : Visibility.Collapsed;
        LinksPage.Visibility = tag == "Links" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
        NoticeBox.Visibility = Visibility.Collapsed;
        if (tag == "Links") RefreshLinks();
        if (tag == "Network") DisplayNetwork();
        MainScroll.ScrollToTop();
    }

    private void BrowseFiles(object sender, RoutedEventArgs e)
    {
        if (_creating) return;
        var dialog = new OpenFileDialog { Title = "Choisir les fichiers à partager", Multiselect = true, Filter = "Tous les fichiers (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
        e.Handled = true;
    }

    private void DropZoneClick(object sender, MouseButtonEventArgs e) => BrowseFiles(sender, e);

    private void DragFilesOver(object sender, DragEventArgs e)
    {
        e.Effects = !_creating && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropFiles(object sender, DragEventArgs e)
    {
        if (!_creating && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var skipped = 0;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { skipped++; continue; }
                _ = info.Length;
                _draft.TryAdd(info.FullName, info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
        }
        UpdateDraft();
        if (skipped != 0) Notice("Certains éléments sont des dossiers ou des fichiers inaccessibles. Choisissez les fichiers à partager.", true);
    }

    private void UpdateDraft()
    {
        var empty = _draft.Count == 0;
        DropZone.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DraftScroll.Visibility = SelectionHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DraftCount.Text = empty ? "" : $"{Display.Count(_draft.Count)} · {Display.Size(_draft.Values.Sum(f => f.Length))}";
        CreateButton.IsEnabled = !empty && !_creating;
        DraftRows.Children.Clear();
        foreach (var file in _draft.Values)
        {
            var row = MakeRow(file.Name, file.Length, "Retirer", () => { _draft.Remove(file.FullName); UpdateDraft(); });
            row.ToolTip = file.FullName;
            DraftRows.Children.Add(row);
        }
    }

    private DockPanel MakeRow(string name, long length, string buttonLabel, Action action)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 3, 3), MinHeight = 36 };
        var button = new Button { Content = buttonLabel, MinHeight = 29, Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{buttonLabel} {name}");
        button.Click += (_, _) => action();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        var badge = new Border { Background = Good, Width = 45, Height = 29, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = extension.Length == 0 ? "FIC" : extension[..Math.Min(extension.Length, 5)], FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold } };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        var size = new TextBlock { Text = Display.Size(length), Style = (Style)FindResource("Hint"), Margin = new Thickness(10, 0, 14, 0) };
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
        return row;
    }

    private async void CreateShare(object sender, RoutedEventArgs e)
    {
        if (_creating || _draft.Count == 0) return;
        _creating = true;
        FileCard.IsEnabled = OptionsCard.IsEnabled = CreateButton.IsEnabled = false;
        var scope = ConnectionMode.SelectedItem == LocalMode ? ShareScope.Local : ShareScope.Internet;
        var hostedRtc = ConnectionMode.SelectedItem == WebRtcMode;
        var publicRtc = ConnectionMode.SelectedItem == PublicRtcMode;
        var minutes = int.Parse((string)((ComboBoxItem)Duration.SelectedItem).Tag);
        CreateButton.Content = scope == ShareScope.Internet ? "Connexion en cours…" : "Création en cours…";
        try
        {
            var files = _draft.Keys.ToArray();
            var duration = TimeSpan.FromMinutes(minutes);
            _createTask = hostedRtc
                ? _internet.CreateAsync(files, duration, RelayOption.IsChecked == true, _lifetime.Token)
                : CreateAddressShareAsync(files, duration, scope, publicRtc);
            var share = await _createTask;
            if (_closing) return;
            _draft.Clear();
            RefreshLinks();
            LinksNav.IsChecked = true;
            var copied = Copy(share.Link.AbsoluteUri);
            var message = copied ? "Lien copié." : "Lien créé. Sélectionnez son adresse pour la copier.";
            if (share.UsesPublicWebRtc && _internet.IsOffline(share.Token))
                message += " WebRTC est momentanément indisponible. Le lien utilise HTTPS.";
            Notice(message);
        }
        catch (Exception ex) { if (!_closing) Notice(Friendly(ex), true); }
        finally
        {
            _creating = false;
            _createTask = null;
            FileCard.IsEnabled = OptionsCard.IsEnabled = true;
            CreateButton.Content = "Créer le lien";
            UpdateDraft();
        }
    }

    private async Task<Share> CreateAddressShareAsync(string[] files, TimeSpan duration, ShareScope scope, bool publicRtc)
    {
        var port = _settings.Port;
        var share = await _sharing.CreateAsync(files, duration, scope, port, _settings.Manual, _lifetime.Token,
            scope == ShareScope.Internet ? Firewall.EnsureInboundAsync : null,
            publicWebRtc: publicRtc);
        if (publicRtc) await _internet.PreparePublicAsync(share, _lifetime.Token);
        return share;
    }

    private void RefreshLinks()
    {
        var active = _sharing.Active.Where(s => !_internet.IsStarting(s.Token)).Concat(_internet.Active).OrderBy(s => s.ExpiresAt).ToArray();
        _tray.Update(!IsVisible, active.Length, _creating || _receiveTask is not null);
        var key = string.Join('/', active.Select(s => s.Token + (_internet.IsOffline(s.Token) ? "-" : "+")));
        if (key == _lastLinks)
        {
            RefreshDownloadCounters();
            return;
        }
        _lastLinks = key;
        NavCount.Text = active.Length == 0 ? "" : $"  {active.Length}";
        LinkCards.Children.Clear();
        _downloadCounters.Clear();
        if (active.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(4, 9, 4, 9) };
            empty.Children.Add(new TextBlock { Text = "Aucun lien actif", Style = (Style)FindResource("SectionTitle") });
            var start = new Button { Content = "Choisir des fichiers", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 15, 0, 0) };
            start.Click += (_, _) => { SendNav.IsChecked = true; BrowseFiles(start, new RoutedEventArgs()); };
            empty.Children.Add(start);
            LinkCards.Children.Add(Card(empty));
        }
        foreach (var share in active) LinkCards.Children.Add(ShareCard(share));
        RefreshDownloadCounters(force: true);
    }

    private Border ShareCard(Share share)
    {
        var box = new StackPanel();
        var title = share.Name;
        box.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.Join("\n", share.Files.Select(f => f.Name)), Style = (Style)FindResource("SectionTitle") });
        var until = share.ExpiresAt.LocalDateTime.ToString("dd MMM à HH:mm");
        var route = _internet.IsOffline(share.Token) ? "Reconnexion en cours…" : share.RouteLabel;
        if (share.UsesPublicWebRtc) route = _internet.IsOffline(share.Token) ? "Secours HTTPS" : $"WebRTC · {share.RouteLabel}";
        box.Children.Add(new TextBlock { Text = $"{Display.Count(share.Files.Count)} · {Display.Size(share.TotalSize)} · {route} · Expire le {until}", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 10) });
        if (share.Details.Description is { } description)
            box.Children.Add(new TextBlock { Text = description, ToolTip = description, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)FindResource("Hint"), MaxHeight = 58, Margin = new Thickness(0, 0, 0, 8) });
        var url = new TextBox { IsReadOnly = true, Text = share.Link.AbsoluteUri, FontSize = 13 };
        url.GotKeyboardFocus += (_, _) => url.SelectAll();
        System.Windows.Automation.AutomationProperties.SetName(url, "Adresse du lien temporaire");
        box.Children.Add(url);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
        var copy = new Button { Content = "Copier le lien", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Notice(Copy(share.Link.AbsoluteUri) ? "Lien copié dans le presse-papiers." : "Sélectionnez l’adresse puis utilisez Ctrl+C.");
        var stop = new Button { Content = "Arrêter le partage" };
        var preview = new Button { Content = "Ouvrir ici", Margin = new Thickness(0, 0, 8, 0) };
        preview.Click += async (_, _) =>
        {
            try
            {
                var page = _internet.PublicPreview(share);
                if (page is null && InternetSharing.IsBrowserLink(share.Link.AbsoluteUri, out var remote))
                    page = await _internet.ReaderUriAsync(remote!, _lifetime.Token);
                if (page is not null)
                {
                    Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
                    return;
                }
                ReceiveNav.IsChecked = true;
                ReceiveUrl.Text = share.Link.AbsoluteUri;
                InspectLink(preview, new RoutedEventArgs());
            }
            catch (Exception ex) { Notice(Friendly(ex), true); }
        };
        stop.Click += async (_, _) =>
        {
            stop.IsEnabled = false;
            try
            {
                if (InternetSharing.IsBrowserLink(share.Link.AbsoluteUri, out _)) _internet.Revoke(share.Token);
                else
                {
                    _internet.RemovePublic(share.Token);
                    await _sharing.RevokeAsync(share.Token);
                }
                RefreshLinks(); Notice("Partage arrêté.");
            }
            catch (Exception ex) { Notice(Friendly(ex), true); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(preview); buttons.Children.Add(stop); box.Children.Add(buttons);
        var stats = new Button { Content = "Téléchargements", Style = (Style)FindResource("TextAction"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        stats.Click += (_, _) => ShowDownloads(share);
        _downloadCounters[share.HistoryId] = stats;
        var edit = new Button { Content = "Modifier le lien", Style = (Style)FindResource("TextAction"), VerticalAlignment = VerticalAlignment.Center };
        edit.Click += (_, _) => EditShareDetails(share);
        var details = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(edit, Dock.Right);
        details.Children.Add(edit);
        details.Children.Add(stats);
        box.Children.Add(details);
        if (share.Scope == ShareScope.Local)
            box.Children.Add(new TextBlock { Text = "Lien local. Connectez les deux appareils au même réseau.", Style = (Style)FindResource("Hint") });
        else if (!InternetSharing.IsBrowserLink(share.Link.AbsoluteUri, out _))
            box.Children.Add(new TextBlock { Text = "Si le navigateur signale le certificat du PC, ouvrez le lien dans Recevoir pour vérifier son empreinte.", Style = (Style)FindResource("Hint") });
        return Card(box);
    }

    private Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content, Margin = new Thickness(0, 0, 0, 12) };

    private static bool Copy(string value)
    {
        try { Clipboard.SetDataObject(value, true); return true; }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_maintaining || _closing) return;
        _maintaining = true;
        try
        {
            if (await _sharing.MaintainAsync()) Notice("La redirection de la box a changé. Recréez un lien Internet.", true);
            if (_closing) return;
            await _internet.CollectAsync(_creating);
            RefreshLinks();
        }
        catch (Exception ex) { if (!_closing) Notice(Friendly(ex), true); }
        finally { _maintaining = false; }
        if (!IsVisible && !HasWork) await QuitAsync();
    }

    private void Notice(string text, bool problem = false)
    {
        NoticeText.Text = text;
        NoticeBox.Background = problem ? Alert : Good;
        NoticeBox.Visibility = Visibility.Visible;
    }

    private void DismissNotice(object sender, RoutedEventArgs e) => NoticeBox.Visibility = Visibility.Collapsed;

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "Le certificat du PC ne correspond pas au lien, ou la connexion chiffrée a échoué. Demandez un nouveau lien.",
        HttpRequestException => "Le PC expéditeur est injoignable. Vérifiez que Passload est ouvert et que le port est accessible depuis votre réseau.",
        OperationCanceledException => "La connexion a été annulée ou le PC n’a pas répondu à temps.",
        _ => ex.Message
    };

    private bool HasWork => _creating || _receiveTask is not null || _sharing.Active.Length != 0 || _internet.Active.Length != 0;

    public void Reopen()
    {
        if (_closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        RefreshLinks();
        Activate();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (HasWork)
        {
            Hide();
            RefreshLinks();
            return;
        }
        await QuitAsync();
    }

    private async void ExitFromTray()
    {
        if (_closing) return;
        if (HasWork && MessageBox.Show("Quitter Passload arrête tous les partages et transferts en cours. Voulez-vous quitter ?",
            "Quitter Passload", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await QuitAsync();
    }

    internal void PrepareForSystemExit()
    {
        _readyToClose = true;
        _timer.Stop();
        _tray.Dispose();
    }

    private async Task QuitAsync()
    {
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        _timer.Stop();
        _lifetime.Cancel();
        _receiveStop?.Cancel();
        try
        {
            if (_receiveTask is { } work) { try { await work; } catch { } }
            if (_createTask is { } creating) { try { await creating; } catch { } }
            await _internet.DisposeAsync();
            await _sharing.DisposeAsync();
        }
        finally
        {
            _receiver?.Dispose();
            _lifetime.Dispose();
            PrepareForSystemExit();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
