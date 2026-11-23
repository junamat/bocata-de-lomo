using Avalonia.Controls;

namespace RefereeHubApi.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainView View => Main;
}
