using System.Windows;
using AutoPower.Core;

namespace AutoPower.App.Services;

/// <summary>
/// Themed message dialogs used instead of System.Windows.MessageBox. Headings and
/// messages are Korean source strings and are translated with <see cref="AppText.T"/>.
/// </summary>
public static class AppDialog
{
    public static void Info(Window? owner, string message, string? heading = null) =>
        MessageDialog.Show(owner ?? ActiveWindow(), Translate(heading), AppText.T(message), DialogKind.Info, AppText.T("확인"));

    public static void Warning(Window? owner, string message, string? heading = null) =>
        MessageDialog.Show(owner ?? ActiveWindow(), Translate(heading), AppText.T(message), DialogKind.Warning, AppText.T("확인"));

    public static void Error(Window? owner, string message, string? heading = null) =>
        MessageDialog.Show(owner ?? ActiveWindow(), Translate(heading), AppText.T(message), DialogKind.Error, AppText.T("확인"));

    /// <summary>Shows an exception message (Korean source text is translated).</summary>
    public static void ShowError(Window? owner, Exception error) =>
        Error(owner, error.Message, "eslee Auto Power");

    /// <summary>Two-button question; true when the user chose the primary button.</summary>
    public static bool Confirm(
        Window? owner,
        string heading,
        string message,
        bool danger = false,
        string primaryText = "예",
        string secondaryText = "아니요") =>
        MessageDialog.Show(
            owner ?? ActiveWindow(),
            Translate(heading),
            AppText.T(message),
            DialogKind.Question,
            AppText.T(primaryText),
            AppText.T(secondaryText),
            danger);

    /// <summary>The focused app window, used as dialog owner when none is given.</summary>
    public static Window? ActiveWindow() =>
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);

    private static string? Translate(string? text) => text is null ? null : AppText.T(text);
}
