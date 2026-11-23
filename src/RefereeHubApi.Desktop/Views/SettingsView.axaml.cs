using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RefereeHubApi.Desktop.ViewModels;

namespace RefereeHubApi.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, captureKey, RoutingStrategies.Tunnel);
    }

    private SettingsViewModel? vm => DataContext as SettingsViewModel;

    private void captureKey(object? sender, KeyEventArgs e)
    {
        if (vm is not { Capturing: true } model)
            return;

        if (e.Key == Key.Escape)
        {
            model.Capturing = false;
            e.Handled = true;
            return;
        }

        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        bool combo = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        string? gesture = MainView.Gesture(e);
        if (gesture == null || (!functionKey && !combo))
            return;

        e.Handled = true;
        model.KeyCaptured(gesture);
    }

    private void keywordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm?.AddKeywordCommand.Execute(null);
        }
    }

    private void milestoneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm?.AddMilestoneCommand.Execute(null);
        }
    }
}
