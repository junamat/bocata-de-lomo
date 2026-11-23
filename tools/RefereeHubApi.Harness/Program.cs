// Dev console for trying the library against the real servers with your own OAuth app.
// Room, chat, timer and log commands come from the library's shared command table; only
// account/connection commands are defined here.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RefereeHubApi.Auth;
using RefereeHubApi.Commands;
using RefereeHubApi.Hub;
using RefereeHubApi.Journal;
using RefereeHubApi.Secrets;
using RefereeHubApi.Session;
using RefereeHubApi.Settings;

string profileName = Environment.GetEnvironmentVariable("REFEREEHUB_PROFILE") ?? "default";
var logLevel = Environment.GetEnvironmentVariable("REFEREEHUB_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Warning;

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(logLevel));
using var http = new HttpClient();
var store = SecretStores.CreateForCurrentOs();
var oauth = new OsuOAuthClient(http);
var json = new JsonSerializerOptions { WriteIndented = false };
var settingsStore = new SettingsStore();

RefereeSettings settings;

try
{
    settings = await settingsStore.LoadAsync();
}
catch (SettingsException ex)
{
    Console.WriteLine($"{ex.Message} Using defaults.");
    settings = new RefereeSettings();
}

RefereeProfile? profile = await RefereeProfile.LoadAsync(profileName, store, oauth);
bool quit = false;

var context = new CommandContext { Settings = settings, Output = Console.WriteLine };

var table = CommandTable.Default.With(
[
    new CommandDefinition
    {
        Name = "setup",
        Category = "Account",
        Usage = "setup",
        Summary = "store your OAuth app's client ID and secret in the OS keychain",
        Bind = a =>
        {
            a.Expect(0, 0);
            return async (ctx, ct) =>
            {
                string clientId = prompt("OAuth client ID");
                string clientSecret = prompt("OAuth client secret");
                string portText = prompt($"Callback port [{OAuthClientConfig.DEFAULT_CALLBACK_PORT}]", allowEmpty: true);
                int port = string.IsNullOrEmpty(portText) ? OAuthClientConfig.DEFAULT_CALLBACK_PORT : int.Parse(portText);

                profile = await RefereeProfile.CreateAsync(profileName, new OAuthClientConfig { ClientId = clientId, ClientSecret = clientSecret, CallbackPort = port }, store, oauth, cancellationToken: ct);
                ctx.Output($"Saved to the OS keychain. The OAuth app's callback URL must be exactly {profile.Client.RedirectUri}");
            };
        },
    },
    new CommandDefinition
    {
        Name = "login",
        Category = "Account",
        Usage = "login",
        Summary = "log in through the browser",
        Bind = a =>
        {
            a.Expect(0, 0);
            return async (ctx, ct) =>
            {
                await requireProfile().LoginAsync(openBrowser, ct);
                ctx.Output("Logged in; tokens stored in the OS keychain.");
            };
        },
    },
    new CommandDefinition
    {
        Name = "logout",
        Category = "Account",
        Usage = "logout",
        Summary = "remove stored tokens",
        Bind = a =>
        {
            a.Expect(0, 0);
            return async (ctx, ct) =>
            {
                await requireProfile().LogoutAsync(ct);
                ctx.Output("Tokens removed.");
            };
        },
    },
    new CommandDefinition
    {
        Name = "connect",
        Category = "Account",
        Usage = "connect",
        Summary = "connect to the hub and rejoin your rooms",
        Bind = a =>
        {
            a.Expect(0, 0);
            return async (ctx, ct) =>
            {
                if (ctx.Session != null)
                {
                    ctx.Output("Already connected.");
                    return;
                }

                var options = new RefereeSessionOptions { Highlights = settings.Highlights, Journal = new RoomJournal() };
                var s = await RefereeSession.StartAsync(requireProfile(), options, loggerFactory: loggerFactory, cancellationToken: ct);
                ctx.Session = s;
                wire(s);
                ctx.Output($"Connected as {s.Self.Username} ({s.Self.Id}). Rooms joined: {string.Join(", ", s.Rooms.Select(r => r.RoomId))}");
                ctx.Room = s.Rooms.FirstOrDefault();

                if ((await s.Journal!.ListAsync(ct)).Any(e => s.GetRoom(e.RoomId) == null))
                    ctx.Output("Unfinished rooms in the journal: see 'journal'.");
            };
        },
    },
    new CommandDefinition
    {
        Name = "quit",
        Aliases = ["exit"],
        Category = "Account",
        Usage = "quit",
        Summary = "leave the harness (rooms stay open)",
        Bind = a =>
        {
            a.Expect(0, 0);
            return (_, _) =>
            {
                quit = true;
                return Task.CompletedTask;
            };
        },
    },
]);

Console.WriteLine($"RefereeHubApi harness. Profile '{profileName}'{(profile == null ? " (not set up: run 'setup')" : profile.HasTokens ? " (logged in)" : " (run 'login')")}. Settings: {settingsStore.FilePath}. Type 'help'.");
Console.WriteLine("Env: REFEREEHUB_PROFILE=<name>, REFEREEHUB_DEBUG=1");

while (!quit)
{
    Console.Write(context.Room is { HasEnded: false } room ? $"[{room.RoomId}]> " : "> ");
    string? line = Console.ReadLine();
    if (line == null)
        break;

    if (string.IsNullOrWhiteSpace(line))
        continue;

    try
    {
        await table.ExecuteAsync(line, context);
    }
    catch (CommandException ex)
    {
        Console.WriteLine(ex.Message);
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

if (context.Session != null)
    await context.Session.DisposeAsync();
return;

RefereeProfile requireProfile() => profile ?? throw new CommandException("Run 'setup' first.");

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
    s.RoomAdded.Subscribe(attach);
    foreach (var r in s.Rooms)
        attach(r);

    s.RoomRemoved.Subscribe(r =>
    {
        Console.WriteLine($"! room {r.RoomId} gone: {r.State.Ended}");
        if (context.Room == r)
            context.Room = null;
    });
    s.Invitations.Subscribe(i => Console.WriteLine($"! added as referee to room {i.RoomId}: 'join {i.RoomId}' to accept"));
    s.ServerShuttingDown.Subscribe(w => Console.WriteLine($"! SERVER SHUTTING DOWN ({w.TimeRemaining}). Close rooms when you can, then 'reconnect' (or 'reconnect close')."));
    s.ConnectionStatus.Subscribe(st => Console.WriteLine($"* hub {st}"));
    s.BackgroundErrors.Subscribe(e => Console.WriteLine($"* background error: {e.Message}"));
    s.Highlights.Subscribe(h => Console.WriteLine($"! highlight ({h.Kinds}) <{h.Message.Sender?.Username ?? h.Message.SenderId.ToString()}> {h.Message.Content}"));

    if (s.Chat != null)
    {
        s.Chat.Transport.Subscribe(t => Console.WriteLine($"* chat via {t}"));
        s.Chat.PrivateChannelOpened.Subscribe(c => c.NewMessages.Subscribe(m => Console.WriteLine($"  [pm {c.PeerUserId}] <{m.Sender?.Username ?? m.SenderId.ToString()}> {(m.IsAction ? "*" : "")}{m.Content}")));
    }
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
    r.Timer.Ticks.Subscribe(t =>
    {
        if (t.Remaining.TotalSeconds % 10 == 0 || t.Remaining.TotalSeconds <= 5)
            Console.WriteLine($"  [{r.RoomId}] timer {RefereeHubApi.Timers.RefereeTimer.FormatRemaining(t.Remaining)}");
    });
}
