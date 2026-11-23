using System.Collections.Immutable;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using RefereeHubApi.Api;
using RefereeHubApi.Desktop;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Desktop.Views;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Responses;
using RefereeHubApi.Journal;
using RefereeHubApi.Session;
using RefereeHubApi.State;
using RefereeHubApi.Tests;
using RefereeHubApi.Tests.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(RefereeHubApi.Desktop.Tests.TestApp))]

namespace RefereeHubApi.Desktop.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UseSkia()
                     .AfterSetup(_ => Program.UseInvariantNumbers())
                     .WithInterFont()
                     .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

// renders every screen against an in-process fake hub; set REFEREEHUB_SNAPSHOTS=<dir> to keep PNGs.
public sealed class ScreenTests : IAsyncDisposable
{
    private const long room_id = 1284550;

    private readonly StubHttpHandler api = new StubHttpHandler();
    private readonly BindingErrors errors = new BindingErrors();
    private readonly string journalDir = Path.Combine(Path.GetTempPath(), "refereehub-ui-" + Guid.NewGuid().ToString("N"));
    private FakeHubServer? server;

    public ScreenTests()
    {
        Logger.Sink = errors;

        api.OnJson("GET", "/api/v2/me", _ => ApiFixtures.Me);
        api.OnJson("GET", "/api/v2/users", _ => new
        {
            users = players.Select(p => new { id = p.Id, username = p.Name, country_code = p.Country })
                           .Append(new { id = TestIds.SELF, username = "TestRef", country_code = "ES" })
                           .Append(new { id = TestIds.OTHER_REF, username = "Strelok", country_code = "UA" }),
        });
        api.OnJson("POST", "/api/v2/beatmaps/", _ => new { attributes = new { star_rating = 6.42, max_combo = 1310 } });
        api.OnJson("GET", "/api/v2/beatmaps/", r => beatmap(int.Parse(r.RequestUri!.AbsolutePath.Split('/').Last())));
        api.OnJson("GET", "/api/v2/beatmaps", _ => new { beatmaps = maps.Select(m => beatmap(m.Id)) });
    }

    private static readonly (int Id, string Name, string Country, MatchTeam Team, MatchUserStatus Status)[] players =
    [
        (100, "Kirin_", "JP", MatchTeam.Red, MatchUserStatus.Ready),
        (101, "mossbell", "DE", MatchTeam.Red, MatchUserStatus.Ready),
        (102, "Tsukuyo", "KR", MatchTeam.Red, MatchUserStatus.Idle),
        (103, "Orrery", "US", MatchTeam.Blue, MatchUserStatus.Ready),
        (104, "lumen-k", "PL", MatchTeam.Blue, MatchUserStatus.Ready),
        (105, "Haruno", "JP", MatchTeam.Blue, MatchUserStatus.Spectating),
    ];

    private static readonly (int Id, string Title, string Artist, string Diff, string Mods, bool Played)[] maps =
    [
        (4402213, "Glass Garden", "Mira Vale", "Bloom", "", true),
        (4402214, "Static Bloom", "Neon Choir", "Overgrowth", "HD", true),
        (4402215, "Marble Sky", "Ilse", "Firmament", "HR", true),
        (4402217, "Rush Hour Comet", "Velo", "Perihelion", "DT", true),
        (4411820, "Cyber Inductance", "Kasane Signal", "Overload", "HD", false),
        (4402219, "Weightless", "Aster Field", "Zenith", "HR", false),
        (4402220, "Velocity Bloom", "Neon Choir", "Burst", "DT", false),
        (4398004, "Endless Daybreak", "Aster Field", "Last Light", "", false),
    ];

    private static object beatmap(int id)
    {
        var m = maps.FirstOrDefault(x => x.Id == id);
        return new
        {
            id,
            beatmapset_id = id / 10,
            beatmapset = new { id = id / 10, artist = m.Artist ?? "Artist", title = m.Title ?? "Title", creator = "Rinsé" },
            version = m.Diff ?? "Insane",
            difficulty_rating = 6.01,
            total_length = 201,
            hit_length = 195,
            bpm = 178,
            cs = 4.2,
            ar = 9.3,
            accuracy = 8.8,
            drain = 5,
            mode = "osu",
            mode_int = 0,
            max_combo = 1310,
        };
    }

    private static RoomJoinedResponse room(long id) => new RoomJoinedResponse
    {
        RoomId = id,
        ChatChannelId = 555,
        Name = "TEST: (Team Aurora) vs (Team Nightfall)",
        Password = "k7Q2-mx9f",
        MaxParticipants = 8,
        QueueMode = QueueMode.HostOnly,
        State = new MatchState { Type = MatchType.TeamVersus, Locked = true, Slots = players.Select(p => (int?)p.Id).Append(null).Append(null).ToArray() },
        Playlist = maps.Select((m, i) => new PlaylistItem
        {
            PlaylistItemId = i + 1,
            BeatmapId = m.Id,
            Order = i + 1,
            WasPlayed = m.Played,
            RequiredMods = m.Mods.Length == 0 ? [] : [new Mod(m.Mods)],
        }).ToArray(),
        Players = players.Select(p => new Player
        {
            UserId = p.Id,
            Status = p.Status,
            Team = p.Team,
            UserMods = p.Id == 102 ? [new Mod("HD"), new Mod("NF")] : [new Mod("HD")],
        }).ToArray(),
        Referees = [new Referee { UserId = TestIds.SELF }, new Referee { UserId = TestIds.OTHER_REF }],
    };

    private async Task<(MainWindow Window, MainWindowViewModel Vm)> startAsync()
    {
        server = await FakeHubServer.StartAsync();
        server.State.RoomFactory = room;
        server.State.ListedRooms.Add(room_id);

        var services = new AppServices
        {
            Journal = new RoomJournal(journalDir),
            StartSession = (options, ct) =>
            {
                options.Hub = server.ConnectionOptions();
                options.ApiBaseUrl = new Uri("http://api.test/api/v2/");
                options.EnableChat = false;
                return RefereeSession.StartAsync(new FakeCredentials(), options, new HttpClient(api), cancellationToken: ct);
            },
        };
        services.Settings.Macros["intro"] = ["say Welcome! Captains, please /roll", "timer 2m"];
        services.Settings.Macros["broken"] = ["lock", "macro intro", "nosuchcommand"];
        services.Settings.Keybinds["F1"] = "intro";
        services.Settings.Highlights.Keywords = ["pause", "sub"];

        var window = new MainWindow { Width = 1440, Height = 980 };
        services.Shell = window;
        var vm = new MainWindowViewModel(services);
        window.DataContext = vm;
        window.Show();

        await vm.OnSignedInAsync();
        await settle(() => vm.RoomTabs.Count == 1 && vm.RoomTabs[0].CurrentMap != null && vm.RoomTabs[0].Playlist.All(p => !p.Title.StartsWith("b/")));
        return (window, vm);
    }

    private static async Task settle(Func<bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(until(), "UI didn't reach the expected state in time");
    }

    private void snapshot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        string? dir = Environment.GetEnvironmentVariable("REFEREEHUB_SNAPSHOTS");
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        Assert.True(errors.Messages.Count == 0, string.Join("\n", errors.Messages));
    }

    [AvaloniaFact]
    public async Task Setup()
    {
        var services = new AppServices();
        var window = new MainWindow { Width = 1440, Height = 900 };
        services.Shell = window;
        var vm = new MainWindowViewModel(services);
        window.DataContext = vm;
        vm.Page = new SetupViewModel(vm);
        window.Show();
        await Task.Yield();
        snapshot(window, "setup");
    }

    [AvaloniaFact]
    public async Task Rooms()
    {
        var (window, vm) = await startAsync();
        await vm.Services.Journal.SaveAsync(new RoomJournalEntry
        {
            RoomId = 777,
            Name = "TEST: (Team Halcyon) vs (Team Ember)",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            SavedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await vm.RoomsPage!.LoadJournalAsync();
        vm.RoomsPage.OnInvited(1284702);
        vm.ShowRooms();
        Assert.Single(vm.RoomsPage.Rooms);
        snapshot(window, "rooms");
    }

    [AvaloniaFact]
    public async Task MatchRoom()
    {
        var (window, vm) = await startAsync();
        var r = vm.RoomTabs[0];
        r.Room.Transcript.Add(message(1, 100, "we ban HD3"));
        r.Room.Transcript.Add(message(2, 103, "NM2 ban"));
        r.Room.Transcript.Add(message(3, TestIds.SELF, "Aurora 3 - 2 Nightfall. Nightfall to pick."));
        r.Room.Transcript.Add(message(4, 105, "TestRef is velvetfox our sub?"));
        r.OnHighlight(new Highlights.Highlight(Highlights.HighlightKind.Mention, message(4, 105, ""), Chat.ChatChannelKind.Room, room_id, []));
        r.ScoreRed = 3;
        r.ScoreBlue = 2;
        vm.ShowRoom(r);
        await settle(() => r.Chat.Count >= 4);

        Assert.Equal("AURORA", r.RedName);
        Assert.Equal(3, r.Teams[0].Players.Length);
        Assert.Equal("MENTION", r.Chat.Last().Tag);
        Assert.Equal(4, r.PlayedCount);
        Assert.True(r.Locked);
        snapshot(window, "room");

        r.ChatInput = "/ti";
        Assert.True(r.ShowHints);
        r.SelectActivityCommand.Execute(null);
        snapshot(window, "room-activity");
    }

    [AvaloniaFact]
    public async Task Results()
    {
        var (window, vm) = await startAsync();
        var r = vm.RoomTabs[0];
        r.ScoreRed = 3;
        r.ScoreBlue = 3;
        var scores = new[]
        {
            score(103, 830112, 0.9864, "S"), score(100, 812440, 0.9821, "S"), score(101, 744019, 0.9688, "A"),
            score(104, 721905, 0.9702, "A"), score(105, 674661, 0.9678, "A"), score(102, 628447, 0.9127, "F", passed: false),
        };
        var match = new MatchInProgress(5, DateTimeOffset.UtcNow, MatchType.TeamVersus,
            players.ToDictionary(p => p.Id, p => p.Team).ToImmutableDictionary(), ImmutableDictionary<int, byte>.Empty);
        var results = new ResultsViewModel(r, new MatchResult(room_id, 5, match, scores, 6, true));
        vm.ShowResults(results);

        Assert.Equal("NIGHTFALL", results.WinnerName);
        Assert.False(results.Partial);
        snapshot(window, "results");
    }

    [AvaloniaFact]
    public async Task Settings()
    {
        var (window, vm) = await startAsync();
        vm.ShowSettings();
        var s = vm.SettingsPage!;
        s.SelectedMacro = s.Macros.First(m => m.Name == "broken");
        Assert.Equal(2, s.MacroLines.Count(l => l.Bad));
        snapshot(window, "settings");
    }

    private static ChatMessage message(long id, int sender, string text) => new ChatMessage
    {
        MessageId = id,
        SenderId = sender,
        ChannelId = 555,
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(id - 10),
        Content = text,
        Sender = new ApiUser { Id = sender, Username = sender == TestIds.SELF ? "TestRef" : players.First(p => p.Id == sender).Name },
    };

    private static MultiplayerScore score(int user, long total, double acc, string rank, bool passed = true) => new MultiplayerScore
    {
        UserId = user,
        TotalScore = total,
        Accuracy = acc,
        MaxCombo = (int)(total / 650),
        Rank = rank,
        Passed = passed,
        Mods = passed ? [new MultiplayerScoreMod { Acronym = "HD" }] : [new MultiplayerScoreMod { Acronym = "HD" }, new MultiplayerScoreMod { Acronym = "NF" }],
        User = new ApiUser { Id = user, Username = players.First(p => p.Id == user).Name },
    };

    public async ValueTask DisposeAsync()
    {
        Logger.Sink = null;
        if (server != null)
            await server.DisposeAsync();
        if (Directory.Exists(journalDir))
            Directory.Delete(journalDir, true);
    }

    private sealed class BindingErrors : ILogSink
    {
        public List<string> Messages { get; } = new List<string>();

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                lock (Messages)
                    Messages.Add($"[{area}] {source?.GetType().Name}: {messageTemplate} {string.Join(", ", propertyValues)}");
            }
        }
    }
}
