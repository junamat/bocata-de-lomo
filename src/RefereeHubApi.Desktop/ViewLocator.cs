using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Desktop.Views;

namespace RefereeHubApi.Desktop;

// explicit map instead of reflection, so trimming can't break it.
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        SetupViewModel => new SetupView(),
        RoomsViewModel => new RoomsView(),
        RoomViewModel => new RoomView(),
        ResultsViewModel => new ResultsView(),
        SettingsViewModel => new SettingsView(),
        null => null,
        _ => new TextBlock { Text = param.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
