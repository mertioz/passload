using System.ComponentModel;
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
    private readonly ShareService _sharing = new();
    private readonly Dictionary<string, FileInfo> _draft = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _lifetime = new();
    private Settings _settings = Settings.Load();
    private string? _lastLinks;
    private bool _creating, _closing, _readyToClose, _maintaining;
    private static readonly System.Windows.Media.Brush Good = new SolidColorBrush(Color.FromRgb(224, 240, 212));
    private static readonly System.Windows.Media.Brush Alert = new SolidColorBrush(Color.FromRgb(255, 232, 195));

    public MainWindow()
    {
        InitializeComponent();
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
        DraftScroll.Visibility = AddMoreButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
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
        var scope = LocalScope.IsChecked == true ? ShareScope.Local : ShareScope.Internet;
        var minutes = D15.IsChecked == true ? 15 : D24.IsChecked == true ? 1440 : D7.IsChecked == true ? 10080 : 60;
        CreateButton.Content = scope == ShareScope.Internet ? "Connexion en cours…" : "Création en cours…";
        try
        {
            var share = await _sharing.CreateAsync(_draft.Keys.ToArray(), TimeSpan.FromMinutes(minutes), scope, _settings.Port, _settings.Manual, _lifetime.Token);
            if (_closing) return;
            _draft.Clear();
            RefreshLinks();
            LinksNav.IsChecked = true;
            var copied = Copy(share.Link.AbsoluteUri);
            Notice(copied ? "Lien créé et copié. Vous pouvez l’envoyer au destinataire." : "Lien créé. Sélectionnez son adresse pour la copier.");
        }
        catch (Exception ex) { if (!_closing) Notice(Friendly(ex), true); }
        finally
        {
            _creating = false;
            FileCard.IsEnabled = OptionsCard.IsEnabled = true;
            CreateButton.Content = "Créer le lien  ↗";
            UpdateDraft();
        }
    }

    private void RefreshLinks()
    {
        var active = _sharing.Active;
        var key = string.Join('/', active.Select(s => s.Token));
        if (key == _lastLinks) return;
        _lastLinks = key;
        NavCount.Text = active.Length == 0 ? "" : active.Length.ToString();
        StateText.Text = active.Length == 0 ? "En veille" : $"{active.Length} partage{(active.Length > 1 ? "s" : "")} en cours";
        StateDot.Fill = active.Length == 0 ? Brushes.DarkGray : (System.Windows.Media.Brush)FindResource("Lime");
        LinkCards.Children.Clear();
        if (active.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(4, 9, 4, 9) };
            empty.Children.Add(new TextBlock { Text = "Aucun lien actif", Style = (Style)FindResource("SectionTitle") });
            empty.Children.Add(new TextBlock { Text = "Les liens que vous créez apparaissent ici jusqu’à leur expiration.", Style = (Style)FindResource("Lead") });
            var start = new Button { Content = "Choisir des fichiers", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 15, 0, 0) };
            start.Click += (_, _) => { SendNav.IsChecked = true; BrowseFiles(start, new RoutedEventArgs()); };
            empty.Children.Add(start);
            LinkCards.Children.Add(Card(empty));
        }
        foreach (var share in active) LinkCards.Children.Add(ShareCard(share));
    }

    private Border ShareCard(Share share)
    {
        var box = new StackPanel();
        var title = share.Files[0].Name + (share.Files.Count > 1 ? $"  +{share.Files.Count - 1}" : "");
        box.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.Join("\n", share.Files.Select(f => f.Name)), Style = (Style)FindResource("SectionTitle") });
        var until = share.ExpiresAt.LocalDateTime.ToString("dd MMM à HH:mm");
        box.Children.Add(new TextBlock { Text = $"{Display.Count(share.Files.Count)} · {Display.Size(share.TotalSize)} · {share.RouteLabel} · Expire le {until}", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 10) });
        var url = new TextBox { IsReadOnly = true, Text = share.Link.AbsoluteUri, FontSize = 13 };
        url.GotKeyboardFocus += (_, _) => url.SelectAll();
        System.Windows.Automation.AutomationProperties.SetName(url, "Adresse du lien temporaire");
        box.Children.Add(url);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
        var copy = new Button { Content = "Copier le lien", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Notice(Copy(share.Link.AbsoluteUri) ? "Lien copié dans le presse-papiers." : "Sélectionnez l’adresse puis utilisez Ctrl+C.");
        var stop = new Button { Content = "Arrêter le partage" };
        stop.Click += async (_, _) =>
        {
            stop.IsEnabled = false;
            try { await _sharing.RevokeAsync(share.Token); RefreshLinks(); Notice("Partage arrêté. Le lien est désactivé."); }
            catch (Exception ex) { Notice(Friendly(ex), true); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(stop); box.Children.Add(buttons);
        var tip = share.Scope == ShareScope.Local ? "Ce lien utilise le réseau local. Les deux appareils doivent y être connectés." :
            share.RouteLabel == "IPv6 directe" ? "Le destinataire doit disposer d’IPv6. Autorisez le port TCP dans les pare-feu de la box et du PC." :
            "Vérifiez l’accès depuis un autre réseau, par exemple en 4G.";
        box.Children.Add(new TextBlock { Text = tip + " Dans Passload, « Recevoir » vérifie le certificat. Un navigateur peut demander de comparer son empreinte SHA-256 à celle du lien.", Style = (Style)FindResource("Hint") });
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
            RefreshLinks();
        }
        catch (Exception ex) { if (!_closing) Notice(Friendly(ex), true); }
        finally { _maintaining = false; }
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

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose) return;
        e.Cancel = true;
        if (_closing) return;
        if ((_sharing.Active.Length > 0 || _receiveTask is not null || _creating) &&
            MessageBox.Show(this, "Fermer Passload arrêtera les partages et les transferts en cours. Fermer maintenant ?", "Fermer Passload",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _closing = true;
        IsEnabled = false;
        _timer.Stop();
        _lifetime.Cancel();
        _receiveStop?.Cancel();
        try
        {
            if (_receiveTask is { } work) { try { await work; } catch { } }
            await _sharing.DisposeAsync();
        }
        finally
        {
            _receiver?.Dispose();
            _lifetime.Dispose();
            _readyToClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
