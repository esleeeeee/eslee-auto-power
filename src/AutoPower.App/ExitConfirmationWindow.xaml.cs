using System.Windows;

namespace AutoPower.App;

public partial class ExitConfirmationWindow : Window
{
    public ExitConfirmationWindow() => InitializeComponent();
    public bool DoNotShowAgain => NeverAgain.IsChecked == true;
    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
