using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Passload.Core;

namespace Passload;

public partial class MainWindow
{
    private ShareDetails? _draftDetails;

    private void EditDraftDetails(object sender, RoutedEventArgs e)
    {
        var defaultName = _draft.Count == 0 ? null : Share.DefaultNameFor(_draft.Values.First().Name, _draft.Count);
        if (ShowDetailsEditor(Locale.Text("ShareDetails"), _draftDetails, defaultName) is { } details)
            SetDraftDetails(details);
    }

    private void SetDraftDetails(ShareDetails? details)
    {
        _draftDetails = details;
        DraftDetailsButton.ToolTip = details?.Title;
    }

    private void EditShareDetails(Share share)
    {
        if (ShowDetailsEditor(Locale.Text("EditLink"), share.Details, share.DefaultName) is not { } details) return;
        if (!share.Active)
        {
            Notice(Locale.Text("LinkExpired"), true);
            return;
        }
        share.UpdateDetails(details.Title, details.Description);
        _lastLinks = null;
        RefreshLinks();
        Notice(Locale.Text("LinkUpdated"));
    }

    private ShareDetails? ShowDetailsEditor(string dialogTitle, ShareDetails? saved, string? defaultName)
    {
        var fields = new StackPanel { Margin = new Thickness(22) };
        var title = new TextBox { Text = saved?.Title ?? "", MaxLength = Share.MaxTitleLength, ToolTip = defaultName };
        var description = new TextBox
        {
            Text = saved?.Description ?? "", MaxLength = Share.MaxDescriptionLength,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 111,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(title, "ShareTitle");
        AutomationProperties.SetAutomationId(description, "ShareDescription");
        AutomationProperties.SetName(title, Locale.Text("LinkName"));
        AutomationProperties.SetName(description, Locale.Text("Description"));
        fields.Children.Add(new TextBlock { Text = Locale.Text("LinkName"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        fields.Children.Add(title);
        if (defaultName is not null)
        {
            var placeholder = new TextBlock { Text = defaultName, Style = (Style)FindResource("Hint"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 11) };
            void RefreshPlaceholder() => placeholder.Visibility = string.IsNullOrWhiteSpace(title.Text) ? Visibility.Visible : Visibility.Collapsed;
            title.TextChanged += (_, _) => RefreshPlaceholder();
            RefreshPlaceholder();
            fields.Children.Add(placeholder);
        }
        fields.Children.Add(new TextBlock { Text = Locale.Text("Description"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 11, 0, 6) });
        fields.Children.Add(description);
        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Orientation = Orientation.Horizontal, Margin = new Thickness(0, 19, 0, 0) };
        var cancel = new Button { Content = Locale.Text("Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 7, 0) };
        var apply = new Button { Content = Locale.Text("Save"), Style = (Style)FindResource("Primary"), IsDefault = true, MinWidth = 125 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        fields.Children.Add(buttons);
        var dialog = new Window
        {
            Title = dialogTitle, Owner = this, Content = fields,
            Width = 446, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Brushes.White, Foreground = (Brush)FindResource("Ink"), FontFamily = FontFamily
        };
        ShareDetails? result = null;
        apply.Click += (_, _) =>
        {
            try
            {
                result = new ShareDetails(title.Text, description.Text);
                dialog.DialogResult = true;
            }
            catch (ArgumentException ex) { MessageBox.Show(dialog, ex.Message, dialogTitle); }
        };
        dialog.Loaded += (_, _) => { title.Focus(); title.SelectAll(); };
        _dialogOpen = true;
        try { return dialog.ShowDialog() == true ? result : null; }
        finally { _dialogOpen = false; }
    }
}
