using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RefereeHubApi.Auth;
using RefereeHubApi.Commands;
using RefereeHubApi.Journal;
using RefereeHubApi.Secrets;
using RefereeHubApi.Session;
using RefereeHubApi.Settings;

namespace RefereeHubApi.Desktop.Services;

// the library objects the whole app shares, plus session lifecycle.
public sealed class AppServices : IAsyncDisposable
{
    private CancellationTokenSource? saveDelay;

    public AppServices()
    {
        ProfileName = Environment.GetEnvironmentVariable("REFEREEHUB_PROFILE") ?? "default";
        var level = Environment.GetEnvironmentVariable("REFEREEHUB_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Warning;
        Logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(level));
        OAuth = new OsuOAuthClient(Http);
    }

    public string ProfileName { get; set; }

    public HttpClient Http { get; } = new HttpClient();

    public OsuOAuthClient OAuth { get; }

    public ILoggerFactory Logging { get; }

    // null when this OS has no secret store yet; SecretsError says why.
    public ISecretStore? Secrets { get; private set; }

    public string? SecretsError { get; private set; }

    public SettingsStore SettingsStore { get; init; } = new SettingsStore();

    public RefereeSettings Settings { get; private set; } = new RefereeSettings();

    public string? SettingsWarning { get; private set; }

    public RoomJournal Journal { get; set; } = new RoomJournal();

    public CommandTable Commands { get; } = CommandTable.Default;

    public RefereeProfile? Profile { get; set; }

    public RefereeSession? Session { get; private set; }

    public IUiShell Shell { get; set; } = null!;

    // tests point the session at a fake hub and API instead of the profile and production.
    internal Func<RefereeSessionOptions, CancellationToken, Task<RefereeSession>>? StartSession { get; set; }

    public string LogDirectory
    {
        get
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(string.IsNullOrEmpty(docs) ? Environment.CurrentDirectory : docs, "RefereeHub");
        }
    }

    // raised on the UI thread with a ready-to-show message.
    public event Action<string>? ErrorReported;

    public void ReportError(Exception ex) => ReportError(Ui.Describe(ex));

    public void ReportError(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ErrorReported?.Invoke(message);
        else
            Dispatcher.UIThread.Post(() => ErrorReported?.Invoke(message));
    }

    public async Task InitAsync()
    {
        try
        {
            Settings = await SettingsStore.LoadAsync();
        }
        catch (SettingsException ex)
        {
            SettingsWarning = $"{ex.Message} Using defaults.";
            Settings = new RefereeSettings();
        }

        try
        {
            Secrets = SecretStores.CreateForCurrentOs();
        }
        catch (PlatformNotSupportedException ex)
        {
            SecretsError = ex.Message;
            return;
        }

        try
        {
            Profile = await RefereeProfile.LoadAsync(ProfileName, Secrets, OAuth);
        }
        catch (Exception ex)
        {
            SecretsError = $"Couldn't read the keychain: {ex.Message}";
        }
    }

    public async Task<RefereeSession> ConnectAsync(CancellationToken ct = default)
    {
        if (Session != null)
            return Session;

        var options = new RefereeSessionOptions { Highlights = Settings.Highlights, Journal = Journal };

        if (StartSession != null)
        {
            Session = await StartSession(options, ct);
            return Session;
        }

        var profile = Profile ?? throw new InvalidOperationException("Not set up.");
        Session = await RefereeSession.StartAsync(profile, options, loggerFactory: Logging, cancellationToken: ct);
        return Session;
    }

    public async Task DisconnectAsync()
    {
        if (Session == null)
            return;

        var s = Session;
        Session = null;
        await s.DisposeAsync();
    }

    public CommandContext CreateCommandContext(RefereeRoom? room, Action<string> output) => new CommandContext
    {
        Session = Session,
        Room = room,
        Settings = Settings,
        Output = output,
        LogDirectory = LogDirectory,
    };

    // settings are saved half a second after the last change.
    public void SaveSettingsSoon()
    {
        saveDelay?.Cancel();
        var cts = saveDelay = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                await SettingsStore.SaveAsync(Settings, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportError($"Couldn't save settings: {ex.Message}");
            }
        });
    }

    public static Task OpenBrowserFallback(Uri uri)
    {
        if (OperatingSystem.IsLinux())
            Process.Start(new ProcessStartInfo("xdg-open", uri.ToString()) { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        Http.Dispose();
        Logging.Dispose();
    }
}
