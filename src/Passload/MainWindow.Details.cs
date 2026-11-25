using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Passload.Core;

namespace Passload;

public partial class MainWindow
{
    private void EditShareDetails(Share share)
    {
        var fields = new StackPanel { Margin = new Thickness(22) };
        var saved = share.Details;
        var title = new TextBox { Text = saved.Title ?? "", MaxLength = Share.MaxTitleLength, ToolTip = share.DefaultName };
        var description = new TextBox
        {
            Text = saved.Description ?? "", MaxLength = Share.MaxDescriptionLength,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 111,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(title, "ShareTitle");
        AutomationProperties.SetAutomationId(description, "ShareDescription");
        AutomationProperties.SetName(title, "Nom du lien");
        AutomationProperties.SetName(description, "Description");
        fields.Children.Add(new TextBlock { Text = "Nom du lien", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        fields.Children.Add(title);
        var placeholder = new TextBlock { Text = share.DefaultName, Style = (Style)FindResource("Hint"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 11), Visibility = title.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        title.TextChanged += (_, _) => placeholder.Visibility = title.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        fields.Children.Add(placeholder);
        fields.Children.Add(new TextBlock { Text = "Description", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 11, 0, 6) });
        fields.Children.Add(description);
        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Orientation = Orientation.Horizontal, Margin = new Thickness(0, 19, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Margin = new Thickness(0, 0, 7, 0) };
        var apply = new Button { Content = "Enregistrer", Style = (Style)FindResource("Primary"), IsDefault = true, MinWidth = 125 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        fields.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Modifier le lien", Owner = this, Content = fields,
            Width = 446, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Brushes.White, Foreground = (Brush)FindResource("Ink"), FontFamily = FontFamily
        };
        apply.Click += (_, _) =>
        {
            if (!share.Active) { dialog.DialogResult = false; Notice("Le lien a expiré.", true); return; }
            try
            {
                share.UpdateDetails(title.Text, description.Text);
                dialog.DialogResult = true;
            }
            catch (ArgumentException ex) { MessageBox.Show(dialog, ex.Message, "Modifier le lien"); }
        };
        dialog.Loaded += (_, _) => { title.Focus(); title.SelectAll(); };
        if (dialog.ShowDialog() == true)
        {
            _lastLinks = null;
            RefreshLinks();
            Notice("Lien mis à jour.");
        }
    }
}
