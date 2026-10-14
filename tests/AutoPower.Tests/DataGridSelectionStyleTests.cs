using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AutoPowerApp = AutoPower.App.App;

namespace AutoPower.Tests;

/// <summary>
/// Light/dark palette and shared control styles (Themes/*.xaml): both palettes define the same keys,
/// the eslee series colors are used, text meets WCAG contrast, and grid selection keeps readable text.
/// </summary>
[TestClass]
public sealed class DataGridSelectionStyleTests
{
    private const double MinimumContrastRatio = 4.5;

    private static readonly Lazy<ThemeSnapshot> Snapshot = new(LoadSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record ThemeSnapshot(
        IReadOnlyDictionary<string, Color> Light,
        IReadOnlyDictionary<string, Color> Dark,
        Color CellBackground,
        Color CellBorder,
        string? CellForegroundKey,
        int HoverTriggerIndex,
        int SelectedTriggerIndex,
        int InactiveTriggerIndex);

    [TestMethod]
    public void LightAndDarkPalettesDefineTheSameKeys()
    {
        var snapshot = Snapshot.Value;
        CollectionAssert.AreEquivalent(snapshot.Light.Keys.ToArray(), snapshot.Dark.Keys.ToArray());
    }

    [TestMethod]
    [DataRow("WindowBackgroundBrush", "#F3F3F3", "#202020")]
    [DataRow("CardBackgroundBrush", "#FFFFFF", "#2B2B2B")]
    [DataRow("CardBackgroundSecondaryBrush", "#F9F9F9", "#262626")]
    [DataRow("CardStrokeBrush", "#E3E3E3", "#363636")]
    [DataRow("TextPrimaryBrush", "#1B1B1B", "#FFFFFF")]
    [DataRow("TextSecondaryBrush", "#5C5C5C", "#C8C8C8")]
    [DataRow("AccentBrush", "#005FB8", "#60CDFF")]
    [DataRow("AccentHoverBrush", "#196EBF", "#5ABBEA")]
    [DataRow("AccentPressedBrush", "#3183CA", "#52A8D4")]
    [DataRow("AccentSubtleBrush", "#E5EFF9", "#1F3443")]
    [DataRow("SuccessForegroundBrush", "#0F7B0F", "#6CCB5F")]
    [DataRow("SuccessBackgroundBrush", "#DFF6DD", "#2A3A23")]
    [DataRow("CautionForegroundBrush", "#9D5D00", "#FCE100")]
    [DataRow("CautionBackgroundBrush", "#FFF4CE", "#433519")]
    public void PaletteUsesSeriesColors(string key, string light, string dark)
    {
        var snapshot = Snapshot.Value;
        Assert.AreEqual((Color)ColorConverter.ConvertFromString(light), snapshot.Light[key], $"light {key}");
        Assert.AreEqual((Color)ColorConverter.ConvertFromString(dark), snapshot.Dark[key], $"dark {key}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TextMeetsContrastOnEverySurface(bool dark)
    {
        var palette = dark ? Snapshot.Value.Dark : Snapshot.Value.Light;
        var window = palette["WindowBackgroundBrush"];
        var card = palette["CardBackgroundBrush"];
        foreach (var surface in new[] { "WindowBackgroundBrush", "PaneBackgroundBrush", "CardBackgroundBrush", "CardBackgroundSecondaryBrush", "ListItemSelectedBrush", "ListItemSelectedHoverBrush" })
        {
            AssertContrast(palette["TextPrimaryBrush"], palette[surface], $"primary text on {surface}");
            AssertContrast(palette["TextSecondaryBrush"], palette[surface], $"secondary text on {surface}");
        }

        AssertContrast(palette["TextPrimaryBrush"], Composite(palette["ListItemHoverBrush"], card), "primary text on hover");
        AssertContrast(palette["TextOnAccentBrush"], palette["AccentBrush"], "text on accent");
        AssertContrast(palette["AccentTextBrush"], card, "accent text on card");
        AssertContrast(palette["AccentTextBrush"], palette["AccentSubtleBrush"], "accent text on subtle accent");
        AssertContrast(palette["SuccessForegroundBrush"], palette["SuccessBackgroundBrush"], "success badge");
        AssertContrast(palette["CautionForegroundBrush"], palette["CautionBackgroundBrush"], "caution badge");
        AssertContrast(palette["CriticalForegroundBrush"], palette["CriticalBackgroundBrush"], "critical badge");
        AssertContrast(palette["NeutralBadgeForegroundBrush"], palette["NeutralBadgeBackgroundBrush"], "neutral badge");
        AssertContrast(palette["TextPrimaryBrush"], window, "primary text on window");
    }

    [TestMethod]
    public void CellStyleBlocksThemeSelectionBrushes()
    {
        var snapshot = Snapshot.Value;
        Assert.AreEqual(Colors.Transparent, snapshot.CellBackground);
        Assert.AreEqual(Colors.Transparent, snapshot.CellBorder);
        Assert.AreEqual("TextPrimaryBrush", snapshot.CellForegroundKey);
    }

    [TestMethod]
    public void RowStyleDefinesHoverActiveAndInactiveSelectionStates()
    {
        var snapshot = Snapshot.Value;
        Assert.IsGreaterThanOrEqualTo(0, snapshot.HoverTriggerIndex);
        Assert.IsGreaterThanOrEqualTo(0, snapshot.SelectedTriggerIndex);
        Assert.IsGreaterThan(snapshot.SelectedTriggerIndex, snapshot.InactiveTriggerIndex, "The inactive-selection trigger must follow the selected trigger.");
    }

    private static int FindInactiveTriggerIndex(Style row) =>
        Enumerable.Range(0, row.Triggers.Count).FirstOrDefault(index =>
            row.Triggers[index] is MultiTrigger trigger &&
            trigger.Conditions.Any(c => c.Property == DataGridRow.IsSelectedProperty && Equals(c.Value, true)) &&
            trigger.Conditions.Any(c => c.Property == Selector.IsSelectionActiveProperty && Equals(c.Value, false)) &&
            SetterResourceKey(trigger.Setters, Control.BackgroundProperty) == "ListItemSelectedHoverBrush", -1);

    private static int FindTriggerIndex(Style style, DependencyProperty property, string brushKey)
    {
        for (var index = 0; index < style.Triggers.Count; index++)
        {
            if (style.Triggers[index] is Trigger trigger &&
                trigger.Property == property &&
                Equals(trigger.Value, true) &&
                SetterResourceKey(trigger.Setters, Control.BackgroundProperty) == brushKey)
            {
                return index;
            }
        }

        return -1;
    }

    private static Color SetterColor(Style style, DependencyProperty property)
    {
        var setter = style.Setters.OfType<Setter>().Single(item => item.Property == property);
        return setter.Value switch
        {
            SolidColorBrush brush => brush.Color,
            Color color => color,
            _ => throw new AssertFailedException($"{property.Name} is not a solid color.")
        };
    }

    private static string? SetterResourceKey(SetterBaseCollection setters, DependencyProperty property) =>
        setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == property)?.Value is DynamicResourceExtension resource
            ? resource.ResourceKey as string
            : null;

    private static Color Composite(Color overlay, Color background)
    {
        var alpha = overlay.A / 255.0;
        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * alpha) + (bottom * (1 - alpha)));
        return Color.FromRgb(Mix(overlay.R, background.R), Mix(overlay.G, background.G), Mix(overlay.B, background.B));
    }

    private static void AssertContrast(Color foreground, Color background, string state)
    {
        var ratio = ContrastRatio(foreground, background);
        Assert.IsGreaterThanOrEqualTo(
            MinimumContrastRatio,
            ratio,
            $"Contrast for {state} is {ratio:F2}, below the required {MinimumContrastRatio:F1}.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
    {
        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

        static double Channel(byte value)
        {
            var scaled = value / 255.0;
            return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }
    }

    private static ThemeSnapshot LoadSnapshot()
    {
        ThemeSnapshot? snapshot = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                snapshot = CaptureSnapshot();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Failed to load theme resources for style verification.", failure);
        }

        return snapshot ?? throw new InvalidOperationException("Theme snapshot was not captured.");
    }

    private static ThemeSnapshot CaptureSnapshot()
    {
        _ = Application.Current as AutoPowerApp ?? new AutoPowerApp();
        var light = Load("Themes/Light.xaml");
        var dark = Load("Themes/Dark.xaml");
        var controls = Load("Themes/Controls.xaml");
        var cell = (Style)controls[typeof(DataGridCell)];
        var row = (Style)controls[typeof(DataGridRow)];
        return new ThemeSnapshot(
            Colors(light),
            Colors(dark),
            SetterColor(cell, Control.BackgroundProperty),
            SetterColor(cell, Control.BorderBrushProperty),
            SetterResourceKey(cell.Setters, Control.ForegroundProperty),
            FindTriggerIndex(row, UIElement.IsMouseOverProperty, "ListItemHoverBrush"),
            FindTriggerIndex(row, DataGridRow.IsSelectedProperty, "ListItemSelectedBrush"),
            FindInactiveTriggerIndex(row));

        static ResourceDictionary Load(string path) =>
            (ResourceDictionary)Application.LoadComponent(new Uri($"/AutoPower.App;component/{path}", UriKind.Relative));

        static IReadOnlyDictionary<string, Color> Colors(ResourceDictionary dictionary) =>
            dictionary.Keys.OfType<string>().ToDictionary(key => key, key => ((SolidColorBrush)dictionary[key]).Color, StringComparer.Ordinal);
    }
}
