using System.Windows;
using AutoPower.App.Controls;

namespace AutoPower.App;

public partial class ExitConfirmationWindow : ThemedWindow
{
    public ExitConfirmationWindow() => InitializeComponent();
    public bool DoNotShowAgain => NeverAgain.IsChecked == true;
    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
