using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Desktop.Views;

namespace RefereeHubApi.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // lets another entry point (the visual test browser) reuse the app's resources and styles with its own window.
    internal static Func<Window>? MainWindowOverride { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime custom && MainWindowOverride != null)
        {
            custom.MainWindow = MainWindowOverride();
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var window = new MainWindow();
            services.Shell = window.View;
            var vm = new MainWindowViewModel(services);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await services.DisposeAsync();
            _ = vm.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
