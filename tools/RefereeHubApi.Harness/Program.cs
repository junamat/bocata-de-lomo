// Dev console for trying the library against the real servers with your own OAuth app.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RefereeHubApi.Auth;
using RefereeHubApi.Hub;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Secrets;
using RefereeHubApi.Session;
using MatchType = RefereeHubApi.Hub.Models.MatchType;

string profileName = Environment.GetEnvironmentVariable("REFEREEHUB_PROFILE") ?? "default";
var logLevel = Environment.GetEnvironmentVariable("REFEREEHUB_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Warning;

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(logLevel));
using var http = new HttpClient();
var store = SecretStores.CreateForCurrentOs();
var oauth = new OsuOAuthClient(http);
var json = new JsonSerializerOptions { WriteIndented = false };

RefereeProfile? profile = await RefereeProfile.LoadAsync(profileName, store, oauth);
RefereeSession? session = null;
RefereeRoom? current = null;

Console.WriteLine($"RefereeHubApi harness. Profile '{profileName}'{(profile == null ? " (not set up: run 'setup')" : profile.HasTokens ? " (logged in)" : " (run 'login')")}. Type 'help'.");

while (true)
{
    Console.Write(current == null ? "> " : $"[{current.RoomId}]> ");
    string? line = Console.ReadLine();
    if (line == null)
        break;

    string[] parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
        continue;

    string cmd = parts[0].ToLowerInvariant();
    string rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;
    string[] argv = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    try
    {
        switch (cmd)
        {
            case "help":
                printHelp();
                break;

            case "quit":
            case "exit":
                goto end;

            case "setup":
            {
                string clientId = prompt("OAuth client ID");
                string clientSecret = prompt("OAuth client secret");
                string portText = prompt($"Callback port [{OAuthClientConfig.DEFAULT_CALLBACK_PORT}]", allowEmpty: true);
                int port = string.IsNullOrEmpty(portText) ? OAuthClientConfig.DEFAULT_CALLBACK_PORT : int.Parse(portText);

                profile = await RefereeProfile.CreateAsync(profileName, new OAuthClientConfig { ClientId = clientId, ClientSecret = clientSecret, CallbackPort = port }, store, oauth);
                Console.WriteLine($"Saved to the OS keychain. The OAuth app's callback URL must be exactly {profile.Client.RedirectUri}");
                break;
            }

            case "login":
                requireProfile();
                await profile!.LoginAsync(openBrowser);
                Console.WriteLine("Logged in; tokens stored in the OS keychain.");
                break;

            case "logout":
                requireProfile();
                await profile!.LogoutAsync();
                Console.WriteLine("Tokens removed.");
                break;

            case "connect":
            {
                requireProfile();
                if (session != null)
                {
                    Console.WriteLine("Already connected.");
                    break;
                }

                session = await RefereeSession.StartAsync(profile!, loggerFactory: loggerFactory);
                wire(session);
                Console.WriteLine($"Connected as {session.Self.Username} ({session.Self.Id}). Rooms joined: {string.Join(", ", session.Rooms.Select(r => r.RoomId))}");
                current = session.Rooms.FirstOrDefault();
                break;
            }

            case "rooms":
            {
                var s = requireSession();
                var listed = await s.ListRoomsAsync();
                Console.WriteLine($"Referee of: {string.Join(", ", listed)}");
                foreach (var r in s.Rooms)
                    Console.WriteLine($"  joined {r.RoomId} '{r.State.Name}' players={r.State.Players.Count} chat={(r.Chat != null ? "on" : "off")}");
                break;
            }

            case "make":
            {
                var s = requireSession();
                if (argv.Length < 3)
                    throw new ArgumentException("make <beatmap_id> <ruleset_id> <name...>");

                current = await s.MakeRoomAsync(new MakeRoomRequest
                {
                    BeatmapId = int.Parse(argv[0]),
                    RulesetId = int.Parse(argv[1]),
                    RoomName = string.Join(' ', argv.Skip(2)),
                });
                Console.WriteLine($"Room {current.RoomId} created. Password: {current.State.Password}");
                break;
            }

            case "join":
                current = await requireSession().JoinRoomAsync(long.Parse(argv[0]));
                Console.WriteLine($"Joined {current.RoomId} '{current.State.Name}'.");
                break;

            case "use":
                current = requireSession().GetRoom(long.Parse(argv[0])) ?? throw new ArgumentException("Not joined to that room.");
                break;

            case "state":
                printState(requireRoom());
                break;

            case "invite":
                await requireRoom().InviteAsync(int.Parse(argv[0]));
                break;

            case "kick":
                await requireRoom().KickAsync(int.Parse(argv[0]));
                break;

            case "ban":
                await requireRoom().BanAsync(int.Parse(argv[0]));
                break;

            case "addref":
                await requireRoom().AddRefereeAsync(int.Parse(argv[0]));
                break;

            case "removeref":
                await requireRoom().RemoveRefereeAsync(int.Parse(argv[0]));
                break;

            case "name":
                await requireRoom().SetNameAsync(rest);
                break;

            case "password":
                await requireRoom().SetPasswordAsync(rest == "-" ? string.Empty : rest);
                break;

            case "type":
                await requireRoom().SetMatchTypeAsync(argv[0] switch
                {
                    "h2h" => MatchType.HeadToHead,
                    "tv" => MatchType.TeamVersus,
                    _ => throw new ArgumentException("type h2h|tv"),
                });
                break;

            case "queue":
                await requireRoom().SetQueueModeAsync(argv[0] switch
                {
                    "host" => QueueMode.HostOnly,
                    "all" => QueueMode.AllPlayers,
                    "rr" => QueueMode.AllPlayersRoundRobin,
                    _ => throw new ArgumentException("queue host|all|rr"),
                });
                break;

            case "size":
                await requireRoom().SetSizeAsync(byte.Parse(argv[0]));
                break;

            case "map":
                await requireRoom().SetMapAsync(int.Parse(argv[0]), argv.Length > 1 ? int.Parse(argv[1]) : null);
                break;

            case "mods":
            {
                if (argv.Length == 0)
                    throw new ArgumentException("mods <required|none> [allowed|none] [freestyle]");

                var required = parseMods(argv[0]);
                var allowed = argv.Length > 1 && argv[1] != "freestyle" ? parseMods(argv[1]) : null;
                bool? freestyle = argv.Contains("freestyle") ? true : null;
                await requireRoom().SetModsAsync(required, allowed, freestyle);
                break;
            }

            case "team":
                await requireRoom().SetTeamAsync(int.Parse(argv[0]), argv[1] == "red" ? MatchTeam.Red : argv[1] == "blue" ? MatchTeam.Blue : throw new ArgumentException("team <user> red|blue"));
                break;

            case "move":
                await requireRoom().MoveAsync(int.Parse(argv[0]), slot: byte.Parse(argv[1]));
                break;

            case "lock":
                await requireRoom().SetLockedAsync(true);
                break;

            case "unlock":
                await requireRoom().SetLockedAsync(false);
                break;

            case "start":
                await requireRoom().StartAsync(argv.Length > 0 ? int.Parse(argv[0]) : null);
                break;

            case "aborttimer":
                await requireRoom().StopCountdownAsync();
                break;

            case "abort":
                await requireRoom().AbortAsync();
                break;

            case "roll":
                await requireRoom().RollAsync(argv.Length > 0 ? uint.Parse(argv[0]) : null);
                break;

            case "say":
                await requireRoom().SendChatAsync(rest);
                break;

            case "me":
                await requireRoom().SendChatAsync(rest, isAction: true);
                break;

            case "scores":
            {
                var room = requireRoom();
                long item = argv.Length > 0 ? long.Parse(argv[0]) : room.State.LastCompletedPlaylistItemId ?? throw new ArgumentException("scores <playlist_item_id>");
                foreach (var sc in (await room.FetchScoresAsync(item)).OrderByDescending(s => s.TotalScore))
                    Console.WriteLine($"  {sc.User?.Username ?? sc.UserId.ToString()}: {sc.TotalScore:N0} {sc.Accuracy:P2} x{sc.MaxCombo} {(sc.Passed ? "" : "FAILED")} [{string.Join(",", sc.Mods.Select(m => m.Acronym))}]");
                break;
            }

            case "close":
                await requireRoom().CloseAsync();
                break;

            case "reconnect":
                await requireSession().ReconnectAsync(closeRoomsFirst: argv.Contains("close"));
                Console.WriteLine("Reconnected.");
                break;

            default:
                Console.WriteLine("Unknown command. Type 'help'.");
                break;
        }
    }
    catch (RefereeHubException ex)
    {
        Console.WriteLine(ex.IsOpaque ? $"Hub error (opaque): {ex.HubMessage}" : $"Hub error {ex.RawCode} ({ex.Code}): {ex.HubMessage}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    }
}

end:
if (session != null)
    await session.DisposeAsync();
return;

void printHelp()
{
    Console.WriteLine("""
        setup | login | logout | connect | rooms | quit
        make <beatmap> <ruleset> <name...> | join <room> | use <room> | state | close
        invite|kick|ban|addref|removeref <user>
        name <text> | password <text|-> | type h2h|tv | queue host|all|rr | size <0|2-16>
        map <beatmap> [ruleset] | mods <HD,HR|none> [allowed|none] [freestyle]
        team <user> red|blue | move <user> <slot> | lock | unlock
        start [seconds] | aborttimer | abort | roll [max]
        say <text> | me <text> | scores [playlist_item] | reconnect [close]
        Env: REFEREEHUB_PROFILE=<name>, REFEREEHUB_DEBUG=1
        """);
}

void requireProfile()
{
    if (profile == null)
        throw new InvalidOperationException("Run 'setup' first.");
}

RefereeSession requireSession() => session ?? throw new InvalidOperationException("Run 'connect' first.");

RefereeRoom requireRoom() => current is { HasEnded: false } ? current : throw new InvalidOperationException("No current room: 'make', 'join' or 'use'.");

string prompt(string label, bool allowEmpty = false)
{
    while (true)
    {
        Console.Write($"{label}: ");
        string value = Console.ReadLine()?.Trim() ?? string.Empty;
        if (value.Length > 0 || allowEmpty)
            return value;
    }
}

static Mod[] parseMods(string text)
    => text == "none" ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => new Mod(a.ToUpperInvariant())).ToArray();

static Task openBrowser(Uri uri)
{
    Console.WriteLine($"Open this URL to log in: {uri}");

    try
    {
        if (OperatingSystem.IsLinux())
            Process.Start(new ProcessStartInfo("xdg-open", uri.ToString()) { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"(could not open a browser automatically: {ex.Message})");
    }

    return Task.CompletedTask;
}

void wire(RefereeSession s)
{
    s.RoomAdded.Subscribe(r => attach(r));
    foreach (var r in s.Rooms)
        attach(r);

    s.RoomRemoved.Subscribe(r =>
    {
        Console.WriteLine($"! room {r.RoomId} gone: {r.State.Ended}");
        if (current == r)
            current = null;
    });
    s.Invitations.Subscribe(i => Console.WriteLine($"! added as referee to room {i.RoomId}: 'join {i.RoomId}' to accept"));
    s.ServerShuttingDown.Subscribe(w => Console.WriteLine($"! SERVER SHUTTING DOWN ({w.TimeRemaining}). Close rooms when you can, then 'reconnect' (or 'reconnect close')."));
    s.ConnectionStatus.Subscribe(st => Console.WriteLine($"* hub {st}"));
    s.BackgroundErrors.Subscribe(e => Console.WriteLine($"* background error: {e.Message}"));
    s.Chat?.Transport.Subscribe(t => Console.WriteLine($"* chat via {t}"));
}

void attach(RefereeRoom r)
{
    r.Events.Subscribe(e => Console.WriteLine($"  [{r.RoomId}] {e.GetType().Name.Replace("Event", "")} {JsonSerializer.Serialize(e, e.GetType(), json)}"));
    r.Results.Subscribe(res =>
    {
        Console.WriteLine($"  [{r.RoomId}] results for item {res.PlaylistItemId} ({res.Scores.Count}/{res.ExpectedScoreCount}{(res.Complete ? "" : ", incomplete")}):");
        foreach (var sc in res.Scores.OrderByDescending(x => x.TotalScore))
        {
            string team = res.Match?.Teams.TryGetValue(sc.UserId, out var t) == true ? $" {t}" : "";
            Console.WriteLine($"    {sc.User?.Username ?? sc.UserId.ToString()}{team}: {sc.TotalScore:N0} {sc.Accuracy:P2}{(sc.Passed ? "" : " FAILED")}");
        }
    });
    r.Chat?.Messages.Subscribe(m => Console.WriteLine($"  [{r.RoomId}] <{m.Sender?.Username ?? m.SenderId.ToString()}> {(m.IsAction ? "*" : "")}{m.Content}"));
}

void printState(RefereeRoom r)
{
    var st = r.State;
    Console.WriteLine($"Room {st.RoomId} '{st.Name}' pw='{st.Password}' {st.MatchType} {st.QueueMode} size={st.MaxParticipants?.ToString() ?? "-"} locked={st.Locked}");
    var item = st.CurrentPlaylistItem;
    Console.WriteLine(item == null ? "Current item: ?" : $"Current item {item.PlaylistItemId}: beatmap {item.BeatmapId} ruleset {item.RulesetId} mods [{string.Join(",", item.RequiredMods.Select(m => m.Acronym))}] allowed [{string.Join(",", item.AllowedMods.Select(m => m.Acronym))}] freestyle={item.Freestyle}");
    Console.WriteLine($"Players ({st.Players.Count}):");
    foreach (var p in st.Players.Values.OrderBy(p => p.Slot ?? 99))
        Console.WriteLine($"  {p.UserId} slot={p.Slot?.ToString() ?? "-"} team={p.Team?.ToString() ?? "-"} {p.Status} mods [{string.Join(",", p.Mods.Select(m => m.Acronym))}]");
    Console.WriteLine($"Referees: {string.Join(", ", st.Referees)} (joined: {string.Join(", ", st.JoinedReferees)})");
    if (st.MatchStartCountdown is { } c)
        Console.WriteLine($"Countdown ends {c.EndsAt:T}");
    if (st.Match is { } m)
        Console.WriteLine($"Match in progress on item {m.PlaylistItemId} since {m.StartedAt:T}");
    if (st.LastRoll is { } roll)
        Console.WriteLine($"Last roll: {roll.UserId} rolled {roll.Result}/{roll.Max}");
}
