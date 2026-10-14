using System.Windows;
using AutoPower.App.Controls;

namespace AutoPower.App;

public enum DialogKind
{
    Info,
    Warning,
    Error,
    Question
}

/// <summary>
/// Themed replacement for the Win32 message box so dialogs follow the app's
/// light/dark theme. Use it through <see cref="Services.AppDialog"/>.
/// </summary>
public partial class MessageDialog : ThemedWindow
{
    private MessageDialog(string? heading, string message, DialogKind kind, string primaryText, string? secondaryText, bool danger, bool defaultToSecondary)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(heading) ? "eslee Auto Power" : heading;
        HeadingText.Text = heading ?? string.Empty;
        HeadingText.Visibility = string.IsNullOrWhiteSpace(heading) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;

        IconGlyph.Text = kind switch
        {
            DialogKind.Error => Glyphs.Error,
            DialogKind.Warning or DialogKind.Question => Glyphs.Warning,
            _ => Glyphs.Info
        };
        IconGlyph.SetResourceReference(ForegroundProperty, kind switch
        {
            DialogKind.Error => "CriticalForegroundBrush",
            DialogKind.Warning or DialogKind.Question => "CautionForegroundBrush",
            _ => "AccentTextBrush"
        });

        PrimaryButton.Content = primaryText;
        PrimaryButton.SetResourceReference(StyleProperty, danger ? "DangerButton" : "AccentButton");
        if (secondaryText is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.IsCancel = true;
            PrimaryButton.Margin = new Thickness(0);
        }
        else
        {
            SecondaryButton.Content = secondaryText;
            if (defaultToSecondary)
            {
                PrimaryButton.IsDefault = false;
                SecondaryButton.IsDefault = true;
                Loaded += (_, _) => SecondaryButton.Focus();
            }
        }
    }

    /// <summary>Shows the dialog modally; true when the primary button was chosen.</summary>
    public static bool Show(
        Window? owner,
        string? heading,
        string message,
        DialogKind kind,
        string primaryText,
        string? secondaryText = null,
        bool danger = false,
        bool topmost = false,
        bool defaultToSecondary = false)
    {
        var dialog = new MessageDialog(heading, message, kind, primaryText, secondaryText, danger, defaultToSecondary) { Topmost = topmost };
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Topmost = topmost || owner.Topmost;
        }
        else
        {
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
