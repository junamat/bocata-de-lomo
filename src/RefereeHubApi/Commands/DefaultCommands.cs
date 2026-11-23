using System.Globalization;
using System.Text;
using RefereeHubApi.Beatmaps;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.IO;
using RefereeHubApi.Logs;
using RefereeHubApi.Session;
using RefereeHubApi.Timers;

namespace RefereeHubApi.Commands
{
    // covers every RefereeRoom action plus chat, PMs, timer, log and macros.
    internal static class DefaultCommands
    {
        private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

        private static readonly Dictionary<string, MatchType> match_types = new Dictionary<string, MatchType>
        {
            ["h2h"] = MatchType.HeadToHead,
            ["tv"] = MatchType.TeamVersus,
        };

        private static readonly Dictionary<string, QueueMode> queue_modes = new Dictionary<string, QueueMode>
        {
            ["host"] = QueueMode.HostOnly,
            ["all"] = QueueMode.AllPlayers,
            ["rr"] = QueueMode.AllPlayersRoundRobin,
        };

        private static readonly Dictionary<string, MatchTeam> teams = new Dictionary<string, MatchTeam>
        {
            ["red"] = MatchTeam.Red,
            ["blue"] = MatchTeam.Blue,
        };

        private static CommandDefinition def(string category, string usage, string summary, Func<CommandArgs, CommandAction> bind, params string[] aliases) => new CommandDefinition
        {
            Name = usage.Split(' ')[0],
            Category = category,
            Usage = usage,
            Summary = summary,
            Bind = bind,
            Aliases = aliases,
        };

        // a command acting on the current room.
        private static CommandAction onRoom(Func<RefereeRoom, CancellationToken, Task> action) => (ctx, ct) => action(ctx.RequireRoom(), ct);

        public static IEnumerable<CommandDefinition> Create()
        {
            const string session = "Session";
            const string players = "Players";
            const string settings = "Room settings";
            const string playlist = "Playlist";
            const string match = "Match";
            const string chat = "Chat";
            const string tools = "Tools";

            #region Session

            yield return def(session, "rooms", "list rooms you're referee of", a =>
            {
                a.Expect(0, 0);
                return async (ctx, ct) =>
                {
                    var s = ctx.RequireSession();
                    var listed = await s.ListRoomsAsync(ct).ConfigureAwait(false);
                    ctx.Output($"Referee of: {(listed.Count == 0 ? "none" : string.Join(", ", listed))}");

                    foreach (var r in s.Rooms)
                        ctx.Output($"  joined {r.RoomId} '{r.State.Name}' players={r.State.Players.Count} chat={(r.Chat != null ? "on" : "off")}{(r == ctx.Room ? " (current)" : "")}");
                };
            });

            yield return def(session, "make <beatmap> <ruleset> <name...>", "create a room (checks the 4-room limit)", a =>
            {
                a.Expect(3);
                int beatmap = a.Int(0, 1);
                int ruleset = a.Int(1, 0, 3);
                string name = a.Text(2);
                return async (ctx, ct) =>
                {
                    var room = await ctx.RequireSession().MakeRoomAsync(new MakeRoomRequest { BeatmapId = beatmap, RulesetId = ruleset, RoomName = name }, ct: ct).ConfigureAwait(false);
                    ctx.Room = room;
                    ctx.Output($"Room {room.RoomId} created. Password: {room.State.Password}");
                };
            });

            yield return def(session, "join <room>", "join a room you're referee of (needed before spectating in lazer)", a =>
            {
                a.Expect(1, 1);
                long id = a.Long(0);
                return async (ctx, ct) =>
                {
                    ctx.Room = await ctx.RequireSession().JoinRoomAsync(id, ct).ConfigureAwait(false);
                    ctx.Output($"Joined {ctx.Room.RoomId} '{ctx.Room.State.Name}'.");
                };
            });

            yield return def(session, "use <room>", "switch the current room", a =>
            {
                a.Expect(1, 1);
                long id = a.Long(0);
                return (ctx, _) =>
                {
                    ctx.Room = ctx.RequireSession().GetRoom(id) ?? throw new CommandException($"Not joined to room {id}.");
                    return Task.CompletedTask;
                };
            });

            yield return def(session, "journal", "rooms left unfinished earlier; 'join <room>' to restore one", a =>
            {
                a.Expect(0, 0);
                return async (ctx, ct) =>
                {
                    var s = ctx.RequireSession();
                    var entries = s.Journal == null ? [] : await s.Journal.ListAsync(ct).ConfigureAwait(false);
                    var open = entries.Where(e => s.GetRoom(e.RoomId) == null).ToArray();

                    if (open.Length == 0)
                        ctx.Output("No unfinished rooms in the journal.");

                    foreach (var e in open)
                        ctx.Output($"  {e.RoomId} '{e.Name}' last seen {e.SavedAt.ToString("yyyy-MM-dd HH:mm", invariant)} UTC, {e.Chat.Count} messages, {e.Results.Count} results");
                };
            });

            yield return def(session, "state", "show the current room", a =>
            {
                a.Expect(0, 0);
                return (ctx, _) =>
                {
                    ctx.Output(FormatState(ctx.RequireRoom().State));
                    return Task.CompletedTask;
                };
            });

            yield return def(session, "close", "close the current room", a =>
            {
                a.Expect(0, 0);
                return onRoom((r, ct) => r.CloseAsync(ct));
            });

            yield return def(session, "reconnect [close]", "reconnect to the hub, optionally closing rooms first", a =>
            {
                a.Expect(0, 1);
                if (a.Has(0) && !a.Contains("close"))
                    throw a.Invalid();
                bool close = a.Contains("close");
                return async (ctx, ct) =>
                {
                    await ctx.RequireSession().ReconnectAsync(close, ct).ConfigureAwait(false);
                    ctx.Output("Reconnected.");
                };
            });

            #endregion

            #region Players

            yield return def(players, "invite <user>", "invite a player (must be online in multiplayer)", a =>
            {
                a.Expect(1, 1);
                var user = a.User(0);
                return onRoom((r, ct) => r.InviteAsync(user, ct));
            });

            yield return def(players, "kick <user>", "kick a player", a =>
            {
                a.Expect(1, 1);
                var user = a.User(0);
                return onRoom((r, ct) => r.KickAsync(user, ct));
            });

            yield return def(players, "ban <user>", "ban a user from the room", a =>
            {
                a.Expect(1, 1);
                var user = a.User(0);
                return onRoom((r, ct) => r.BanAsync(user, ct));
            });

            yield return def(players, "addref <user>", "add a referee (room host only)", a =>
            {
                a.Expect(1, 1);
                var user = a.User(0);
                return onRoom((r, ct) => r.AddRefereeAsync(user, ct));
            });

            yield return def(players, "removeref <user>", "remove a referee (room host only)", a =>
            {
                a.Expect(1, 1);
                var user = a.User(0);
                return onRoom((r, ct) => r.RemoveRefereeAsync(user, ct));
            });

            yield return def(players, "team <user> red|blue", "move a player to a team", a =>
            {
                a.Expect(2, 2);
                var user = a.User(0);
                var team = a.Choice(1, teams);
                return onRoom((r, ct) => r.SetTeamAsync(user, team, ct));
            });

            yield return def(players, "move <user> <slot>", "move a player to a slot (0-15)", a =>
            {
                a.Expect(2, 2);
                var user = a.User(0);
                byte slot = (byte)a.Int(1, 0, 15);
                return onRoom((r, ct) => r.MoveAsync(user, slot: slot, ct: ct));
            });

            yield return def(players, "lock", "lock slots and teams", a =>
            {
                a.Expect(0, 0);
                return onRoom((r, ct) => r.SetLockedAsync(true, ct));
            });

            yield return def(players, "unlock", "unlock slots and teams", a =>
            {
                a.Expect(0, 0);
                return onRoom((r, ct) => r.SetLockedAsync(false, ct));
            });

            #endregion

            #region Settings

            yield return def(settings, "name <text>", "rename the room", a =>
            {
                string name = a.Text(0);
                return onRoom((r, ct) => r.SetNameAsync(name, ct));
            });

            yield return def(settings, "password <text|->", "set the password ('-' removes it)", a =>
            {
                a.Expect(1);
                string text = a.Text(0);
                string password = text == "-" ? string.Empty : text;
                return onRoom((r, ct) => r.SetPasswordAsync(password, ct));
            });

            yield return def(settings, "type h2h|tv", "head to head or team versus", a =>
            {
                a.Expect(1, 1);
                var type = a.Choice(0, match_types);
                return onRoom((r, ct) => r.SetMatchTypeAsync(type, ct));
            });

            yield return def(settings, "queue host|all|rr", "queue mode", a =>
            {
                a.Expect(1, 1);
                var mode = a.Choice(0, queue_modes);
                return onRoom((r, ct) => r.SetQueueModeAsync(mode, ct));
            });

            yield return def(settings, "size <0|2-16>", "player limit (0 removes it)", a =>
            {
                a.Expect(1, 1);
                int size = a.Int(0, 0, 16);
                if (size == 1)
                    throw a.Invalid("size must be 0 or 2-16");
                return onRoom((r, ct) => r.SetSizeAsync((byte)size, ct));
            });

            #endregion

            #region Playlist

            yield return def(playlist, "map <beatmap> [ruleset]", "set the current map (changing ruleset clears mods)", a =>
            {
                a.Expect(1, 2);
                int beatmap = a.Int(0, 1);
                int? ruleset = a.OptionalInt(1, 0, 3);
                return onRoom((r, ct) => r.SetMapAsync(beatmap, ruleset, ct));
            });

            yield return def(playlist, "mods <required|none> [allowed|none] [freestyle]", "set mods on the current map", a =>
            {
                a.Expect(1, 3);
                var required = a.Mods(0);
                bool freestyle = a.Contains("freestyle");
                Mod[]? allowed = a.Has(1) && !string.Equals(a.Token(1), "freestyle", StringComparison.OrdinalIgnoreCase) ? a.Mods(1) : null;

                if (a.Count == 3 && !string.Equals(a.Token(2), "freestyle", StringComparison.OrdinalIgnoreCase))
                    throw a.Invalid();
                if (freestyle && allowed is { Length: > 0 })
                    throw a.Invalid("allowed mods can't be combined with freestyle");

                return onRoom((r, ct) => r.SetModsAsync(required, allowed, freestyle ? true : null, ct));
            });

            yield return def(playlist, "additem <beatmap> [ruleset] [mods]", "add a playlist item", a =>
            {
                a.Expect(1, 3);
                int beatmap = a.Int(0, 1);
                int ruleset = a.OptionalInt(1, 0, 3) ?? 0;
                var mods = a.Has(2) ? a.Mods(2) : [];
                return onRoom((r, ct) => r.AddPlaylistItemAsync(new AddPlaylistItemRequest { BeatmapId = beatmap, RulesetId = ruleset, RequiredMods = mods }, ct));
            });

            yield return def(playlist, "edititem <item> <beatmap> [ruleset] [mods]", "change a playlist item", a =>
            {
                a.Expect(2, 4);
                long item = a.Long(0);
                int beatmap = a.Int(1, 1);
                int? ruleset = a.OptionalInt(2, 0, 3);
                Mod[]? mods = a.Has(3) ? a.Mods(3) : null;
                return onRoom((r, ct) => r.EditPlaylistItemAsync(new EditPlaylistItemRequest { PlaylistItemId = item, BeatmapId = beatmap, RulesetId = ruleset, RequiredMods = mods }, ct));
            });

            yield return def(playlist, "removeitem <item>", "remove a playlist item", a =>
            {
                a.Expect(1, 1);
                long item = a.Long(0);
                return onRoom((r, ct) => r.RemovePlaylistItemAsync(item, ct));
            });

            yield return def(playlist, "info [beatmap] [mods]", "beatmap details with star rating and stats for the mods (default: current item)", a =>
            {
                a.Expect(0, 2);
                int? beatmap = a.OptionalInt(0, 1);
                var mods = a.Has(1) ? a.Mods(1) : null;
                return async (ctx, ct) =>
                {
                    BeatmapInfo info;

                    if (beatmap == null)
                        info = await ctx.RequireRoom().DescribeBeatmapAsync(ct: ct).ConfigureAwait(false);
                    else
                    {
                        int ruleset = ctx.Room?.State.CurrentPlaylistItem?.RulesetId ?? 0;
                        info = await ctx.RequireSession().Beatmaps.DescribeAsync(beatmap.Value, ruleset, mods ?? [], ct).ConfigureAwait(false);
                    }

                    ctx.Output(FormatBeatmap(info));
                };
            });

            #endregion

            #region Match

            yield return def(match, "start [seconds]", "start the match, optionally after a countdown", a =>
            {
                a.Expect(0, 1);
                int? seconds = a.OptionalInt(0, 1, 3600);
                return onRoom((r, ct) => r.StartAsync(seconds, ct));
            });

            yield return def(match, "aborttimer", "stop the start countdown", a =>
            {
                a.Expect(0, 0);
                return onRoom((r, ct) => r.StopCountdownAsync(ct));
            });

            yield return def(match, "abort", "abort the match in progress", a =>
            {
                a.Expect(0, 0);
                return onRoom((r, ct) => r.AbortAsync(ct));
            });

            yield return def(match, "roll [max]", "roll 1-max (default 100)", a =>
            {
                a.Expect(0, 1);
                int? max = a.OptionalInt(0, 1, 100);
                return onRoom((r, ct) => r.RollAsync(max == null ? null : (uint)max, ct));
            });

            yield return def(match, "scores [item]", "scores of a playlist item (default: last completed)", a =>
            {
                a.Expect(0, 1);
                long? item = a.Has(0) ? a.Long(0) : null;
                return async (ctx, ct) =>
                {
                    var room = ctx.RequireRoom();
                    long id = item ?? room.State.LastCompletedPlaylistItemId ?? throw new CommandException("No completed map yet; give a playlist item id.");
                    var scores = await room.FetchScoresAsync(id, ct).ConfigureAwait(false);

                    if (scores.Count == 0)
                        ctx.Output("No scores.");

                    foreach (var sc in scores.OrderByDescending(s => s.TotalScore))
                        ctx.Output($"  {sc.User?.Username ?? sc.UserId.ToString(invariant)}: {sc.TotalScore.ToString("N0", invariant)} {(sc.Accuracy * 100).ToString("0.00", invariant)}% x{sc.MaxCombo}{(sc.Passed ? "" : " FAILED")} [{string.Join(",", sc.Mods.Select(m => m.Acronym))}]");
                };
            });

            #endregion

            #region Chat

            yield return def(chat, "say <text>", "send to room chat", a =>
            {
                string text = a.Text(0);
                return onRoom((r, ct) => r.SendChatAsync(text, ct: ct));
            });

            yield return def(chat, "me <text>", "send an action (/me) to room chat", a =>
            {
                string text = a.Text(0);
                return onRoom((r, ct) => r.SendChatAsync(text, isAction: true, ct));
            });

            yield return new CommandDefinition
            {
                Name = "pm",
                Aliases = ["msg"],
                Category = chat,
                Usage = "pm <user> <text>",
                Summary = "private message one user (names with spaces: use _)",
                Bind = a =>
                {
                    var user = a.User(0);
                    string text = a.Text(1);
                    return (ctx, ct) => ctx.RequireSession().SendPrivateMessageAsync(user, text, ct: ct);
                },
                PrivateMessageTarget = a => a.User(0),
            };

            yield return def(chat, "whois <user>", "look up a user by name or id", a =>
            {
                a.Expect(1, 1);
                var user = a.User(0);
                return async (ctx, ct) =>
                {
                    var users = ctx.RequireSession().Users;
                    var found = user.Id is int id ? await users.GetByIdAsync(id, ct).ConfigureAwait(false) : await users.GetByNameAsync(user.Username!, ct).ConfigureAwait(false);
                    ctx.Output(found == null ? $"No user {user}." : $"{found.Username} (#{found.Id}){(found.CountryCode != null ? $" [{found.CountryCode}]" : "")}");
                };
            });

            #endregion

            #region Tools

            yield return def(tools, "timer <duration>|stop", "local timer (90, 90s, 2m, 1:30); never affects the room", a =>
            {
                a.Expect(1, 1);

                if (string.Equals(a.Token(0), "stop", StringComparison.OrdinalIgnoreCase))
                    return onRoom((r, _) =>
                    {
                        r.StopTimer();
                        return Task.CompletedTask;
                    });

                var duration = a.Duration(0);
                return (ctx, _) =>
                {
                    var room = ctx.RequireRoom();
                    room.StartTimer(duration, ctx.Settings.Timer);
                    ctx.Output($"Timer: {RefereeTimer.FormatRemaining(duration)}.");
                    return Task.CompletedTask;
                };
            });

            yield return def(tools, "log [path]", "save the room's chat log", a =>
            {
                string? path = a.Count > 0 ? a.Text(0) : null;
                return async (ctx, ct) =>
                {
                    var room = ctx.RequireRoom();
                    string target = path ?? Path.Combine(ctx.LogDirectory, $"mp_{room.RoomId}_{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", invariant)}.txt");
                    await AtomicFile.WriteAllTextAsync(target, room.ExportLog(ctx.Settings.ChatLog), ct).ConfigureAwait(false);
                    ctx.Output($"Log written to {Path.GetFullPath(target)}");
                };
            });

            yield return def(tools, "macro <name>", "run a macro from settings", a =>
            {
                a.Expect(1, 1);
                string name = a.Token(0);
                return (ctx, ct) => (ctx.Table ?? CommandTable.Default).RunMacroAsync(name, ctx, ct);
            });

            yield return def(tools, "macros", "list macros", a =>
            {
                a.Expect(0, 0);
                return (ctx, _) =>
                {
                    if (ctx.Settings.Macros.Count == 0)
                        ctx.Output("No macros.");

                    foreach (var (name, lines) in ctx.Settings.Macros)
                        ctx.Output($"  {name}: {string.Join(" ; ", lines)}");

                    return Task.CompletedTask;
                };
            });

            yield return def(tools, "help [command]", "this list", a =>
            {
                a.Expect(0, 1);
                string? command = a.Has(0) ? a.Token(0) : null;
                return (ctx, _) =>
                {
                    ctx.Output((ctx.Table ?? CommandTable.Default).Help(command));
                    return Task.CompletedTask;
                };
            });

            #endregion
        }

        public static string FormatState(State.RoomSnapshot st)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Room {st.RoomId} '{st.Name}' pw='{st.Password}' {st.MatchType} {st.QueueMode} size={st.MaxParticipants?.ToString(invariant) ?? "-"} locked={st.Locked}");

            var item = st.CurrentPlaylistItem;
            sb.AppendLine(item == null
                ? "Current item: ?"
                : $"Current item {item.PlaylistItemId}: beatmap {item.BeatmapId} ruleset {item.RulesetId} mods [{string.Join(",", item.RequiredMods.Select(m => m.Acronym))}] allowed [{string.Join(",", item.AllowedMods.Select(m => m.Acronym))}] freestyle={item.Freestyle}");

            sb.AppendLine($"Players ({st.Players.Count}):");
            foreach (var p in st.Players.Values.OrderBy(p => p.Slot ?? 99))
                sb.AppendLine($"  {p.UserId} slot={p.Slot?.ToString(invariant) ?? "-"} team={p.Team?.ToString() ?? "-"} {p.Status} mods [{string.Join(",", p.Mods.Select(m => m.Acronym))}]");

            sb.Append($"Referees: {string.Join(", ", st.Referees)} (joined: {string.Join(", ", st.JoinedReferees)})");

            if (st.MatchStartCountdown is { } c)
                sb.Append($"\nCountdown ends {c.EndsAt:T}");
            if (st.Match is { } m)
                sb.Append($"\nMatch in progress on item {m.PlaylistItemId} since {m.StartedAt:T}");
            if (st.LastRoll is { } roll)
                sb.Append($"\nLast roll: {roll.UserId} rolled {roll.Result}/{roll.Max}");

            return sb.ToString();
        }

        public static string FormatBeatmap(BeatmapInfo info)
        {
            var d = info.Modded;
            string mods = info.Mods.Count > 0 ? $" +{string.Join("", info.Mods.Select(m => m.Acronym))}" : string.Empty;
            string length = TimeSpan.FromSeconds(Math.Round(d.TotalLength)).ToString(@"m\:ss", invariant);

            string stats = info.DifficultyAdjusted
                ? $"CS {d.CircleSize.ToString("0.##", invariant)} AR {d.ApproachRate.ToString("0.##", invariant)} OD {d.OverallDifficulty.ToString("0.##", invariant)}"
                : $"CS {d.CircleSize.ToString("0.##", invariant)} OD {d.OverallDifficulty.ToString("0.##", invariant)} (unadjusted for this ruleset)";

            return $"{info.Beatmap} (#{info.Beatmap.Id}){mods}\n  {info.StarRating.ToString("0.00", invariant)}* {stats} BPM {d.Bpm.ToString("0.#", invariant)} length {length}{(info.MaxCombo is int combo ? $" max combo {combo}" : "")}";
        }
    }
}
