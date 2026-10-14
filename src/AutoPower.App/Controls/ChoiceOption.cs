namespace AutoPower.App.Controls;

/// <summary>
/// ComboBox option with a translated label. <see cref="ToString"/> returns the label
/// so screen readers and UI Automation read a meaningful name.
/// </summary>
public sealed record ChoiceOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
