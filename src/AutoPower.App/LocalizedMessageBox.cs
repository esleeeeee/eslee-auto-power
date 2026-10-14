using System.Windows;
using AutoPower.App.Services;
using AutoPower.Core;

namespace AutoPower.App;

/// <summary>
/// MessageBox-shaped facade over the themed <see cref="MessageDialog"/>. Texts are Korean
/// source strings translated with <see cref="AppText.T"/>. Only OK and YesNo are supported.
/// </summary>
internal static class LocalizedMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult) =>
        Show(null, messageBoxText, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var kind = icon switch
        {
            MessageBoxImage.Error => DialogKind.Error,
            MessageBoxImage.Warning => DialogKind.Warning,
            MessageBoxImage.Question => DialogKind.Question,
            _ => DialogKind.Info
        };
        var heading = string.Equals(caption, "eslee Auto Power", StringComparison.Ordinal) ? null : AppText.T(caption);
        var dialogOwner = owner ?? AppDialog.ActiveWindow();
        if (button == MessageBoxButton.YesNo)
        {
            return MessageDialog.Show(
                dialogOwner,
                heading,
                AppText.T(messageBoxText),
                kind,
                AppText.T("예"),
                AppText.T("아니요"),
                defaultToSecondary: defaultResult == MessageBoxResult.No)
                ? MessageBoxResult.Yes
                : MessageBoxResult.No;
        }

        MessageDialog.Show(dialogOwner, heading, AppText.T(messageBoxText), kind, AppText.T("확인"));
        return MessageBoxResult.OK;
    }
}
