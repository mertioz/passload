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
    private bool _creating, _closing, _readyToClose, _maintaining, _dialogOpen;
    private static readonly System.Windows.Media.Brush Good = new SolidColorBrush(Color.FromRgb(241, 243, 240));
    private static readonly System.Windows.Media.Brush Alert = new SolidColorBrush(Color.FromRgb(253, 241, 229));

    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Passload");
    public MainWindow() : this(new DownloadHistory(Path.Combine(AppData, "download-history-tls.jsonl"),
        Path.Combine(AppData, "download-history.jsonl"))) { }

    internal MainWindow(DownloadHistory history)
    {
        _history = history;
        _sharing = new ShareService(history);
        ApplyLanguage(_settings.Language);
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Locale.Current.Name);
        SelectLanguage();
        if (WindowsPackage.HasIdentity) FirewallButton.Visibility = Visibility.Collapsed;
        _internet = new InternetSharing(Surface, _sharing);
        _tray = new ShareTray(Reopen, ExitFromTray);
        SendNav.IsChecked = true;
        PortInput.Text = _settings.Port.ToString();
        ManualInput.Text = _settings.Manual;
        PublicHostInput.Text = _settings.PublicHost ?? "";
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
        var dialog = new OpenFileDialog { Title = Locale.Text("ChooseFiles"), Multiselect = true, Filter = Locale.Text("AllFiles") + "|*.*" };
        if (SelectFile(dialog)) AddFiles(dialog.FileNames);
        e.Handled = true;
    }

    private bool SelectFile(FileDialog dialog)
    {
        _dialogOpen = true;
        try { return dialog.ShowDialog(this) == true; }
        finally { _dialogOpen = false; }
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
        if (skipped != 0) Notice(Locale.Text("SkippedFiles"), true);
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
            var row = MakeRow(file.Name, file.Length, Locale.Text("Remove"), () => { _draft.Remove(file.FullName); UpdateDraft(); });
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
            Child = new TextBlock { Text = extension.Length == 0 ? Locale.Text("FileBadge") : extension[..Math.Min(extension.Length, 5)], FontSize = 12,
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
        var scope = ConnectionMode.SelectedItem == PrivateMode ? ShareScope.Local : ShareScope.Internet;
        var minutes = int.Parse((string)((ComboBoxItem)Duration.SelectedItem).Tag);
        CreateButton.SetResourceReference(ContentControl.ContentProperty, scope == ShareScope.Internet ? "Text.Connecting" : "Text.CreatingLink");
        try
        {
            var files = _draft.Keys.ToArray();
            var duration = TimeSpan.FromMinutes(minutes);
            _createTask = CreateAddressShareAsync(files, duration, scope, _draftDetails);
            var share = await _createTask;
            if (_closing) return;
            _draft.Clear();
            SetDraftDetails(null);
            RefreshLinks();
            LinksNav.IsChecked = true;
            var copied = Copy(share.PublishedLink.AbsoluteUri);
            var message = Locale.Text(copied ? "LinkCopied" : "LinkCreated");
            if (share.UsesPublicWebRtc && _internet.IsOffline(share.Token))
                message += " " + Locale.Text("WebRtcFallback");
            Notice(message);
        }
        catch (Exception ex) { if (!_closing) Notice(Friendly(ex), true); }
        finally
        {
            _creating = false;
            _createTask = null;
            FileCard.IsEnabled = OptionsCard.IsEnabled = true;
            CreateButton.SetResourceReference(ContentControl.ContentProperty, "Text.CreateLink");
            UpdateDraft();
        }
    }

    private async Task<Share> CreateAddressShareAsync(string[] files, TimeSpan duration, ShareScope scope, ShareDetails? details)
    {
        var port = _settings.Port;
        var publicNetwork = scope == ShareScope.Internet;
        var share = await _sharing.CreateAsync(files, duration, scope, port, _settings.Manual, _lifetime.Token,
            publicNetwork ? Firewall.EnsureInboundAsync : null,
            publicWebRtc: publicNetwork, publicHost: _settings.PublicHost, details: details);
        if (share.UsesPublicWebRtc) await _internet.PreparePublicAsync(share, _lifetime.Token);
        return share;
    }

    private void RefreshLinks()
    {
        var active = _sharing.Active.Where(s => !_internet.IsStarting(s.Token)).ToArray();
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
            empty.Children.Add(new TextBlock { Text = Locale.Text("NoActiveLinks"), Style = (Style)FindResource("SectionTitle") });
            var start = new Button { Content = Locale.Text("SelectFiles"), Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 15, 0, 0) };
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
        var until = Locale.Text("ExpiresAt", share.ExpiresAt.LocalDateTime.ToString("g", Locale.Current.Culture));
        var route = share.RouteLabel;
        if (share.UsesPublicWebRtc)
            route = _internet.IsOffline(share.Token) ? Locale.Text("HttpsFallback") : $"WebRTC · {share.RouteLabel}";
        box.Children.Add(new TextBlock { Text = $"{Display.Count(share.Files.Count)} · {Display.Size(share.TotalSize)} · {route} · {until}", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 10) });
        if (share.Details.Description is { } description)
            box.Children.Add(new TextBlock { Text = description, ToolTip = description, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)FindResource("Hint"), MaxHeight = 58, Margin = new Thickness(0, 0, 0, 8) });
        var url = new TextBox { IsReadOnly = true, Text = share.PublishedLink.AbsoluteUri, FontSize = 13 };
        url.GotKeyboardFocus += (_, _) => url.SelectAll();
        System.Windows.Automation.AutomationProperties.SetName(url, Locale.Text("TemporaryLinkAddress"));
        box.Children.Add(url);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
        var copy = new Button { Content = Locale.Text("CopyLink"), Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Notice(Copy(share.PublishedLink.AbsoluteUri) ? Locale.Text("LinkCopied") : Locale.Text("CopyAddressManually"));
        var stop = new Button { Content = Locale.Text("StopSharing") };
        var preview = new Button { Content = Locale.Text("OpenHere"), Margin = new Thickness(0, 0, 8, 0) };
        preview.Click += (_, _) =>
        {
            try
            {
                var page = _internet.PublicPreview(share);
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
                _internet.RemovePublic(share.Token);
                await _sharing.RevokeAsync(share.Token);
                RefreshLinks(); Notice(Locale.Text("SharingStopped"));
            }
            catch (Exception ex) { Notice(Friendly(ex), true); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(preview); buttons.Children.Add(stop); box.Children.Add(buttons);
        var stats = new Button { Content = Locale.Text("Downloads"), Style = (Style)FindResource("TextAction"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        stats.Click += (_, _) => ShowDownloads(share);
        _downloadCounters[share.HistoryId] = stats;
        var edit = new Button { Content = Locale.Text("EditLink"), Style = (Style)FindResource("TextAction"), VerticalAlignment = VerticalAlignment.Center };
        edit.Click += (_, _) => EditShareDetails(share);
        var details = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(edit, Dock.Right);
        details.Children.Add(edit);
        details.Children.Add(stats);
        box.Children.Add(details);
        if (share.Scope == ShareScope.Local)
            box.Children.Add(new TextBlock { Text = Locale.Text("LocalLinkHint"), Style = (Style)FindResource("Hint") });
        else if (!share.UsesTrustedHttps)
            box.Children.Add(new TextBlock { Text = Locale.Text("VerifyCertificateHint"), Style = (Style)FindResource("Hint") });
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
            if (await _sharing.MaintainAsync()) Notice(Locale.Text("ForwardingChanged"), true);
            if (_closing) return;
            await _internet.CollectAsync(_creating);
            RefreshLinks();
            if (DateTimeOffset.UtcNow >= _nextUpdateCheck) _ = CheckUpdatesAsync(manual: false);
            await ApplyPendingUpdateAsync();
        }
        catch (Exception ex) { if (!_closing) Notice(Friendly(ex), true); }
        finally { _maintaining = false; }
        if (!_closing && !IsVisible && !HasWork) await QuitAsync();
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
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => Locale.Text("TlsError"),
        HttpRequestException => Locale.Text("SenderUnreachable"),
        OperationCanceledException => Locale.Text("ConnectionCanceled"),
        _ => ex.Message
    };

    private bool HasWork => _creating || _receiveTask is not null || _sharing.Active.Length != 0;

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
        if (_closing || _installingUpdate) return;
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
        if (_closing || _installingUpdate) return;
        if (HasWork && MessageBox.Show(Locale.Text("ConfirmQuit"),
            Locale.Text("QuitPassload"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
