using Avalonia.Controls;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Desktop.Views;
using RefereeHubApi.Hub.Models.Responses;
using RefereeHubApi.Journal;
using RefereeHubApi.Session;
using RefereeHubApi.Settings;
using RefereeHubApi.Tests;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.VisualTests.Framework;

// the real app UI wired to an in-process fake hub and stubbed osu! API. Every test gets a fresh app,
// hub and data folder from the set-up steps, so tests don't leak state into each other. Settings and the
// journal live in a throwaway folder, so scenes never touch the user's real files.
public abstract class AppScene : VisualScene
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "refereehub-scene-" + Guid.NewGuid().ToString("N"));
    private readonly ContentControl host = new ContentControl();
    private int builds;

    // shared by every test in the scene; register extra endpoints from a test method.
    protected StubHttpHandler Api { get; } = new StubHttpHandler();

    protected FakeHubServer Server { get; private set; } = null!;

    protected AppServices Services { get; private set; } = null!;

    protected MainWindowViewModel Main { get; private set; } = null!;

    protected MainView View { get; private set; } = null!;

    // the first room tab, once the "room loaded" step has passed.
    protected RoomViewModel Room => Main.RoomTabs[0];

    // false: the scene drives sign-in itself (or never signs in).
    protected virtual bool ConnectOnStart => true;

    // rooms the fake hub lists, so the app rejoins them on connect.
    protected virtual long[] ListedRooms => [Fixtures.ROOM_ID];

    protected virtual RoomJoinedResponse CreateRoom(long id) => Fixtures.Room(id);

    protected virtual void ConfigureSettings(RefereeSettings settings)
    {
        settings.Macros["intro"] = ["say Welcome! Captains, please /roll", "timer 2m"];
        settings.Macros["broken"] = ["lock", "macro intro", "nosuchcommand"];
        settings.Keybinds["F1"] = "intro";
        settings.Highlights.Keywords = ["pause", "sub"];
    }

    // replace to make sign-in fail, etc.
    protected virtual Task<RefereeSession> StartSessionAsync(RefereeSessionOptions options, CancellationToken ct)
    {
        options.Hub = Server.ConnectionOptions();
        options.ApiBaseUrl = new Uri("http://api.test/api/v2/");
        options.EnableChat = false;
        return RefereeSession.StartAsync(new FakeCredentials(), options, new HttpClient(Api), cancellationToken: ct);
    }

    public override Task<Control> CreateAsync()
    {
        Fixtures.StubApi(Api);
        return Task.FromResult<Control>(host);
    }

    [SetUpSteps]
    public void SetUpApp()
    {
        AddStep("create app", createAppAsync);

        if (!ConnectOnStart)
            return;

        AddStep("connect to fake hub", () => Main.OnSignedInAsync());
        if (ListedRooms.Length > 0)
            AddUntilStep("room loaded", () => Main.RoomTabs.Count == ListedRooms.Length
                                             && Main.RoomTabs.All(r => r.CurrentMap != null && r.Playlist.All(p => !p.Title.StartsWith("b/"))));
    }

    private async Task createAppAsync()
    {
        await teardownAppAsync();

        string dataDir = Path.Combine(scratch, (++builds).ToString());

        Server = await FakeHubServer.StartAsync();
        Server.State.RoomFactory = CreateRoom;
        Server.State.ListedRooms.AddRange(ListedRooms);

        Services = new AppServices
        {
            Journal = new RoomJournal(Path.Combine(dataDir, "journal")),
            SettingsStore = new SettingsStore(Path.Combine(dataDir, "settings.json")),
            StartSession = StartSessionAsync,
        };
        ConfigureSettings(Services.Settings);

        View = new MainView();
        Services.Shell = View;
        Main = new MainWindowViewModel(Services);
        View.DataContext = Main;
        host.Content = View;
    }

    private async Task teardownAppAsync()
    {
        host.Content = null;
        Main?.Dispose();
        if (Services != null)
            await Services.DisposeAsync();
        if (Server != null)
            await Server.DisposeAsync();
        Main = null!;
        Services = null!;
        Server = null!;
    }

    protected Task SendAsync(string hubEvent, object payload) => Server.SendAsync(hubEvent, payload);

    public override async ValueTask DisposeAsync()
    {
        await teardownAppAsync();

        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, true);
        }
        catch (IOException)
        {
        }
    }
}
