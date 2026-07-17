using System.Windows;
using AutoPower.Core;

namespace AutoPower.App;

internal static class LocalizedMessageBox
{
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(AppText.T(messageBoxText), AppText.T(caption), button, icon);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult) =>
        System.Windows.MessageBox.Show(AppText.T(messageBoxText), AppText.T(caption), button, icon, defaultResult);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(owner, AppText.T(messageBoxText), AppText.T(caption), button, icon);

    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult) =>
        System.Windows.MessageBox.Show(owner, AppText.T(messageBoxText), AppText.T(caption), button, icon, defaultResult);
}
