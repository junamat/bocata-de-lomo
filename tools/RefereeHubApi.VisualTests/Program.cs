using Avalonia;
using RefereeHubApi.Desktop;
using RefereeHubApi.VisualTests.Browser;

namespace RefereeHubApi.VisualTests;

// dotnet run --project tools/RefereeHubApi.VisualTests [-- --scene "Match room"]
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        RefereeHubApi.Desktop.Program.UseInvariantNumbers();

        // the fake hub is an in-process ASP.NET app; its per-request logs drown everything else.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");

        string? scene = null;
        int at = Array.IndexOf(args, "--scene");
        if (at >= 0 && at + 1 < args.Length)
            scene = args[at + 1];

        App.MainWindowOverride = () => new BrowserWindow { DataContext = new BrowserViewModel(scene) };

        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace()
                  .StartWithClassicDesktopLifetime(args);
    }
}
