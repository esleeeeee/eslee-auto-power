using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AutoPowerApp = AutoPower.App.App;

namespace AutoPower.Tests;

[TestClass]
public sealed class DataGridSelectionStyleTests
{
    private const double MinimumContrastRatio = 4.5;

    private static readonly Lazy<StyleSnapshot> Snapshot = new(LoadSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record StyleSnapshot(
        Color Text,
        Color RowBackground,
        Color AlternatingRowBackground,
        Color Hover,
        Color SelectionActive,
        Color SelectionInactive,
        Color CellForeground,
        Color CellBackground,
        Color CellBorder,
        bool RowHoverTriggerUsesHoverBrush,
        bool RowSelectedTriggerUsesSelectionBrush,
        bool RowInactiveTriggerUsesInactiveBrush,
        bool InactiveTriggerFollowsSelectedTrigger);

    [TestMethod]
    public void SelectionBrushResourcesExistInAppTheme()
    {
        var snapshot = Snapshot.Value;

        Assert.AreNotEqual(default, snapshot.Hover);
        Assert.AreNotEqual(default, snapshot.SelectionActive);
        Assert.AreNotEqual(default, snapshot.SelectionInactive);
        Assert.AreNotEqual(snapshot.SelectionActive, snapshot.SelectionInactive);
    }

    [TestMethod]
    public void RowStyleDefinesHoverActiveAndInactiveSelectionStates()
    {
        var snapshot = Snapshot.Value;

        Assert.IsTrue(snapshot.RowHoverTriggerUsesHoverBrush);
        Assert.IsTrue(snapshot.RowSelectedTriggerUsesSelectionBrush);
        Assert.IsTrue(snapshot.RowInactiveTriggerUsesInactiveBrush);
        Assert.IsTrue(snapshot.InactiveTriggerFollowsSelectedTrigger);
    }

    [TestMethod]
    public void CellStyleBlocksThemeSelectionBrushes()
    {
        var snapshot = Snapshot.Value;

        Assert.AreEqual(Colors.Transparent, snapshot.CellBackground);
        Assert.AreEqual(Colors.Transparent, snapshot.CellBorder);
        Assert.AreEqual(snapshot.Text, snapshot.CellForeground);
    }

    [TestMethod]
    public void RowTextMeetsContrastInEveryRowState()
    {
        var snapshot = Snapshot.Value;

        AssertContrast(snapshot.Text, snapshot.RowBackground, "row background");
        AssertContrast(snapshot.Text, snapshot.AlternatingRowBackground, "alternating row background");
        AssertContrast(snapshot.Text, snapshot.Hover, "hover background");
        AssertContrast(snapshot.Text, snapshot.SelectionActive, "active selection background");
        AssertContrast(snapshot.Text, snapshot.SelectionInactive, "inactive selection background");
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

    private static StyleSnapshot LoadSnapshot()
    {
        StyleSnapshot? snapshot = null;
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
            throw new InvalidOperationException("Failed to load App.xaml resources for style verification.", failure);
        }

        return snapshot ?? throw new InvalidOperationException("App.xaml style snapshot was not captured.");
    }

    private static StyleSnapshot CaptureSnapshot()
    {
        var app = Application.Current as AutoPowerApp ?? new AutoPowerApp();
        app.InitializeComponent();
        var resources = app.Resources;

        var text = BrushColor(resources, "TextBrush");
        var hover = BrushColor(resources, "GridHoverBrush");
        var selectionActive = BrushColor(resources, "GridSelectionBrush");
        var selectionInactive = BrushColor(resources, "GridSelectionInactiveBrush");

        var gridStyle = (Style)resources[typeof(DataGrid)];
        var rowBackground = SetterColor(gridStyle, DataGrid.RowBackgroundProperty);
        var alternatingRowBackground = SetterColor(gridStyle, DataGrid.AlternatingRowBackgroundProperty);

        var cellStyle = (Style)resources[typeof(DataGridCell)];
        var cellForeground = SetterColor(cellStyle, DataGridCell.ForegroundProperty);
        var cellBackground = SetterColor(cellStyle, DataGridCell.BackgroundProperty);
        var cellBorder = SetterColor(cellStyle, DataGridCell.BorderBrushProperty);

        var rowStyle = (Style)resources[typeof(DataGridRow)];
        var hoverTriggerIndex = FindTriggerIndex(rowStyle, UIElement.IsMouseOverProperty, expectedValue: true, hover);
        var selectedTriggerIndex = FindTriggerIndex(rowStyle, DataGridRow.IsSelectedProperty, expectedValue: true, selectionActive);
        var inactiveTriggerIndex = FindInactiveSelectionTriggerIndex(rowStyle, selectionInactive);

        return new StyleSnapshot(
            text,
            rowBackground,
            alternatingRowBackground,
            hover,
            selectionActive,
            selectionInactive,
            cellForeground,
            cellBackground,
            cellBorder,
            hoverTriggerIndex >= 0,
            selectedTriggerIndex >= 0,
            inactiveTriggerIndex >= 0,
            inactiveTriggerIndex > selectedTriggerIndex && selectedTriggerIndex >= 0);
    }

    private static Color BrushColor(ResourceDictionary resources, string key)
    {
        Assert.IsTrue(resources.Contains(key), $"App resource '{key}' is missing.");
        return ((SolidColorBrush)resources[key]).Color;
    }

    private static Color SetterColor(Style style, DependencyProperty property)
    {
        var setter = style.Setters.OfType<Setter>().Single(item => item.Property == property);
        return ((SolidColorBrush)setter.Value).Color;
    }

    private static int FindTriggerIndex(Style style, DependencyProperty property, bool expectedValue, Color expectedBackground)
    {
        for (var index = 0; index < style.Triggers.Count; index++)
        {
            if (style.Triggers[index] is Trigger trigger &&
                trigger.Property == property &&
                trigger.Value is bool value &&
                value == expectedValue &&
                TriggerSetsBackground(trigger.Setters, expectedBackground))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindInactiveSelectionTriggerIndex(Style style, Color expectedBackground)
    {
        for (var index = 0; index < style.Triggers.Count; index++)
        {
            if (style.Triggers[index] is MultiTrigger trigger &&
                HasCondition(trigger, DataGridRow.IsSelectedProperty, expected: true) &&
                HasCondition(trigger, Selector.IsSelectionActiveProperty, expected: false) &&
                TriggerSetsBackground(trigger.Setters, expectedBackground))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasCondition(MultiTrigger trigger, DependencyProperty property, bool expected)
    {
        return trigger.Conditions.Any(condition =>
            condition.Property == property && condition.Value is bool value && value == expected);
    }

    private static bool TriggerSetsBackground(SetterBaseCollection setters, Color expected)
    {
        return setters.OfType<Setter>().Any(setter =>
            setter.Property == Control.BackgroundProperty &&
            setter.Value is SolidColorBrush brush &&
            brush.Color == expected);
    }
}
