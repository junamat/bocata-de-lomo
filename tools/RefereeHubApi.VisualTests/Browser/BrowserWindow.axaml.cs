using Avalonia.Controls;
using Avalonia.Input;

namespace RefereeHubApi.VisualTests.Browser;

public partial class BrowserWindow : Window
{
    private bool shutDown;

    public BrowserWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, onKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // Ctrl+F: show the list if hidden and jump to search, whatever has focus inside the scene.
    private void onKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not BrowserViewModel vm)
            return;

        e.Handled = true;
        vm.ListVisible = true;
        Search.Focus();
        Search.SelectAll();
    }

    // tear the scene's fake hub down before the process exits.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (shutDown || DataContext is not BrowserViewModel vm)
            return;

        e.Cancel = true;
        shutDown = true;
        await vm.ShutdownAsync();
        Close();
    }
}
