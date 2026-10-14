using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AutoPower.App.Controls;

/// <summary>
/// Converter instances for XAML (x:Static), usable without application
/// resources so windows still load in tests.
/// </summary>
public static class Converters
{
    public static readonly IValueConverter BoolToVisibility = new BoolToVisibilityConverter(invert: false);

    public static readonly IValueConverter InverseBoolToVisibility = new BoolToVisibilityConverter(invert: true);

    public static readonly IValueConverter NullToCollapsed = new NullToVisibilityConverter();

    /// <summary>Visible when the bound count is zero (empty-state text).</summary>
    public static readonly IValueConverter ZeroToVisible = new ZeroToVisibilityConverter();

    private sealed class BoolToVisibilityConverter(bool invert) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is true;
            return flag ^ invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is null || (value is string text && string.IsNullOrWhiteSpace(text)) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
