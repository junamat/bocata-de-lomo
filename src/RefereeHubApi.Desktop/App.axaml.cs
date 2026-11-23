using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Desktop.Views;

namespace RefereeHubApi.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var window = new MainWindow();
            services.Shell = window;
            var vm = new MainWindowViewModel(services);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await services.DisposeAsync();
            _ = vm.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
