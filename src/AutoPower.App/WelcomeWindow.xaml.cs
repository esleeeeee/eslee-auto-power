using System.Windows;

namespace AutoPower.App;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow() => InitializeComponent();
    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
