using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AutoPower.Core;

namespace AutoPower.App;

internal static class WpfLocalizer
{
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled || !AppText.IsEnglish)
        {
            return;
        }

        _enabled = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            Localize(window);
        }
    }

    private static void Localize(Window window)
    {
        var visited = new HashSet<DependencyObject>();
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(window);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            LocalizeObject(current);

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
            {
                pending.Enqueue(child);
            }

            if (current is Visual or Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var index = 0; index < count; index++)
                {
                    pending.Enqueue(VisualTreeHelper.GetChild(current, index));
                }
            }
        }
    }

    private static void LocalizeObject(DependencyObject item)
    {
        if (item is Window window &&
            !BindingOperations.IsDataBound(window, Window.TitleProperty))
        {
            window.SetCurrentValue(Window.TitleProperty, AppText.T(window.Title));
        }

        if (item is TextBlock textBlock &&
            !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty))
        {
            textBlock.SetCurrentValue(TextBlock.TextProperty, AppText.T(textBlock.Text));
        }

        if (item is ContentControl contentControl &&
            contentControl.Content is string content &&
            !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty))
        {
            contentControl.SetCurrentValue(ContentControl.ContentProperty, AppText.T(content));
        }

        if (item is HeaderedContentControl headeredContent && headeredContent.Header is string header)
        {
            headeredContent.SetCurrentValue(HeaderedContentControl.HeaderProperty, AppText.T(header));
        }

        if (item is HeaderedItemsControl headeredItems && headeredItems.Header is string itemsHeader)
        {
            headeredItems.SetCurrentValue(HeaderedItemsControl.HeaderProperty, AppText.T(itemsHeader));
        }

        if (item is FrameworkElement element && element.ToolTip is string tooltip)
        {
            element.SetCurrentValue(FrameworkElement.ToolTipProperty, AppText.T(tooltip));
        }

        if (item is System.Windows.Controls.DataGrid dataGrid)
        {
            foreach (var column in dataGrid.Columns.Where(column => column.Header is string))
            {
                column.Header = AppText.T((string)column.Header);
            }
        }
    }
}
