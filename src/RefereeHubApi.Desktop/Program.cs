using Avalonia;

namespace RefereeHubApi.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        UseInvariantNumbers();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // the UI is English and osu! values read as "4.2", "1,234,567"; keep that whatever the OS locale.
    public static void UseInvariantNumbers()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .LogToTrace();
}
