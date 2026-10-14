using System.Windows;
using System.Windows.Media.Imaging;
using AutoPower.App.Services;

namespace AutoPower.App.Controls;

/// <summary>
/// Base class of every app window: theme background, text color, font and icon,
/// plus a title bar that matches the current light or dark palette.
/// </summary>
public class ThemedWindow : Window
{
    private static readonly Uri IconUri = new("pack://application:,,,/AutoPower.App;component/Assets/eslee-auto-power.ico");

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = ThemeFonts.Text;
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
        try
        {
            Icon = BitmapFrame.Create(IconUri);
        }
        catch (Exception)
        {
            // Pack URIs are unavailable when windows are created without the WPF application (tests).
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }
}
