using System.Windows;
using AutoPower.App.Controls;

namespace AutoPower.App;

public partial class WelcomeWindow : ThemedWindow
{
    public WelcomeWindow() => InitializeComponent();
    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
