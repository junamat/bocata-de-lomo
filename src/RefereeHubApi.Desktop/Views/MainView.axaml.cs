using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Desktop.ViewModels;

namespace RefereeHubApi.Desktop.Views;

// the whole app UI; hosted by MainWindow, or by the visual test browser.
public partial class MainView : UserControl, IUiShell
{
    public MainView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, onKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? vm => DataContext as MainWindowViewModel;

    public static string? Gesture(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
            return null;

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        parts.Add(e.Key.ToString());
        return string.Join("+", parts);
    }

    private async void onKeyDown(object? sender, KeyEventArgs e)
    {
        if (vm is not { IsSignedIn: true } model || e.Handled)
            return;

        // plain typing keys stay with text boxes; F-keys and modifier combos can be bound.
        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        bool combo = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (!functionKey && !combo)
            return;

        if (SettingsViewModel.CapturingKey)
            return;

        string? gesture = Gesture(e);
        if (gesture != null && model.Services.Settings.Keybinds.ContainsKey(gesture))
        {
            e.Handled = true;
            await model.HandleKeyAsync(gesture);
        }
    }

    private void pmOpened(object? sender, EventArgs e) => vm?.PmPanelOpened();

    private void pmClosed(object? sender, EventArgs e) => vm?.PmPanelClosed();

    private void errorsOpened(object? sender, EventArgs e) => vm?.ErrorsOpened();

    private void pmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && vm != null)
        {
            e.Handled = true;
            vm.SendPmCommand.Execute(null);
        }
    }

    public async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public async Task<string?> SaveTextAsync(string suggestedName, string content)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });

        if (file == null)
            return null;

        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(content);

        return file.TryGetLocalPath() ?? file.Name;
    }

    public async Task OpenUrlAsync(Uri uri)
    {
        if (!(TopLevel.GetTopLevel(this) is { } top && await top.Launcher.LaunchUriAsync(uri)))
            await AppServices.OpenBrowserFallback(uri);
    }
}
