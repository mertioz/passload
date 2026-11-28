using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Passload.Core;

namespace Passload;

public partial class MainWindow
{
    private ResourceDictionary? _translations;
    private bool _selectingLanguage;

    private void ApplyLanguage(string? language)
    {
        Locale.Use(language);
        var strings = new ResourceDictionary();
        foreach (var (key, message) in Locale.Current.Messages)
            strings["Text." + key] = message;
        strings["BodyFont"] = Locale.Current.Name switch
        {
            "zh-CN" => new FontFamily("Microsoft YaHei UI"),
            "ja-JP" => new FontFamily("Yu Gothic UI"),
            _ => Application.Current.FindResource("BarlowFont")
        };
        if (_translations is not null) Resources.MergedDictionaries.Remove(_translations);
        Resources.MergedDictionaries.Add(strings);
        _translations = strings;
    }

    private void SelectLanguage()
    {
        _selectingLanguage = true;
        try
        {
            LanguagePicker.Items.Clear();
            LanguagePicker.Items.Add(new ComboBoxItem { Content = Locale.Text("SystemLanguage") });
            foreach (var locale in Locale.Supported)
                LanguagePicker.Items.Add(new ComboBoxItem { Content = locale.NativeName, Tag = locale.Name });
            var saved = Locale.Match(_settings.Language)?.Name;
            LanguagePicker.SelectedItem = LanguagePicker.Items.Cast<ComboBoxItem>()
                .First(item => Equals(item.Tag, saved));
        }
        finally { _selectingLanguage = false; }
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingLanguage || LanguagePicker.SelectedItem is not ComboBoxItem choice) return;
        var next = _settings with { Language = (string?)choice.Tag };
        if (next == _settings) return;
        try { next.Write(); }
        catch (Exception ex) { SelectLanguage(); Notice(Friendly(ex), true); return; }

        _settings = next;
        ApplyLanguage(next.Language);
        Language = XmlLanguage.GetLanguage(Locale.Current.Name);
        SelectLanguage();
        NoticeBox.Visibility = Visibility.Collapsed;
        _lastLinks = null;
        UpdateDraft();
        RefreshLinks();
        RefreshIncoming();
        if (_receiveMessageKey is { } key) DownloadText.Text = Locale.Text(key);
        DisplayNetwork();
    }
}
