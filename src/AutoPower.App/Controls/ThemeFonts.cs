using FontFamily = System.Windows.Media.FontFamily;

namespace AutoPower.App.Controls;

/// <summary>
/// Fonts shared by every window. Exposed as static fields so XAML can use
/// x:Static, which also works when no Application resources are loaded (tests).
/// </summary>
public static class ThemeFonts
{
    /// <summary>Windows 11 icon font with the Windows 10 icon font as fallback.</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>UI text font; Korean glyphs fall back to Malgun Gothic.</summary>
    public static readonly FontFamily Text = new("Segoe UI, Malgun Gothic");

    /// <summary>Tabular digits for times and countdowns.</summary>
    public static readonly FontFamily Numbers = new("Segoe UI, Malgun Gothic");
}

/// <summary>Segoe Fluent Icons code points used by the UI.</summary>
public static class Glyphs
{
    public const string Add = "\uE710";
    public const string Edit = "\uE70F";
    public const string Copy = "\uE8C8";
    public const string Delete = "\uE74D";
    public const string More = "\uE712";
    public const string Settings = "\uE713";
    public const string Search = "\uE721";
    public const string Filter = "\uE71C";
    public const string Calendar = "\uE787";
    public const string Clock = "\uE823";
    public const string History = "\uE81C";
    public const string Repeat = "\uE8EE";
    public const string Pause = "\uE769";
    public const string Play = "\uE768";
    public const string Power = "\uE7E8";
    public const string Sleep = "\uE708";
    public const string Hibernate = "\uE74E";
    public const string Wake = "\uE706";
    public const string Shield = "\uEA18";
    public const string Diagnostic = "\uE9D9";
    public const string Program = "\uE7C4";
    public const string Warning = "\uE7BA";
    public const string Error = "\uE783";
    public const string Info = "\uE946";
    public const string Completed = "\uE930";
    public const string CheckMark = "\uE73E";
    public const string Cancel = "\uE711";
    public const string Sync = "\uE895";
    public const string OpenInNew = "\uE8A7";
    public const string Folder = "\uE8B7";
    public const string Theme = "\uE790";
    public const string Language = "\uF2B7";
    public const string Key = "\uE8D7";
    public const string Contact = "\uE77B";
    public const string Snooze = "\uE916";
}
