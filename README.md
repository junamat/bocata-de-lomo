# RefereeHubApi

Referee tooling for osu! tournaments played in lazer multiplayer. It talks to the referee hub in
[osu-server-spectator](https://github.com/ppy/osu-server-spectator), which replaces the old `!mp` bancho commands for lazer multiplayer.

This repo has the client-side library (everything a ref needs to run a match: make the room, invite players, set maps and mods, start, read chat, get scores, close) and a desktop app built on it with Avalonia.

Besides room control it has: user lookups by name, beatmap info with star rating and mod-adjusted CS/AR/OD, private messages, chat log export, highlights (your name, "ref", keywords, PMs), a local timer, a text command table with macros, a JSON settings file and a journal of unfinished rooms.

## Layout

- `src/RefereeHubApi` - the library
- `src/RefereeHubApi.Desktop` - the desktop app (setup, rooms, match room, map results, settings)
- `tools/RefereeHubApi.Harness` - a small console for trying things against the real servers
- `tools/RefereeHubApi.VisualTests` - a browser of UI scenes: the real app put into known states against a fake hub
- `tests/RefereeHubApi.Tests` - unit tests
- `tests/RefereeHubApi.HubTests` - tests against an in-process fake hub (needs the ASP.NET Core runtime)
- `tests/RefereeHubApi.Desktop.Tests` - runs every visual test scene headless against the fake hub

## Requirements

- .NET 10 SDK
- Linux for now: tokens are stored with libsecret (GNOME Keyring / KWallet). Windows and macOS storage isn't written yet.
- An osu! OAuth application of your own (see below)

## Setting up as a ref

Every ref uses their own osu! account and their own OAuth app. Without a bot account, osu-web only issues the scopes the hub needs to the owner of the OAuth app, so sharing one app between refs doesn't work.

1. Go to your osu! account settings > OAuth > New OAuth Application.
2. Set the callback URL to `http://localhost:7270/callback` (the port can be changed, but it must match exactly in both places).
3. Start the app and fill in the client ID and secret, then sign in with osu! (or, in the harness, `setup` then `login`).

The client secret and tokens are stored in your OS keychain, nowhere else.

```
dotnet run --project src/RefereeHubApi.Desktop
```

The app ships the Outfit, Quicksand and JetBrains Mono fonts (see `src/RefereeHubApi.Desktop/Assets/Fonts`), so it looks the same on every machine. In the match room, chat lines starting with `/` run the same commands as the harness (`/help` lists them). Scores per map come from the server; the match score (best-of pips) is kept by the ref with the +/- buttons. Alert sounds can be picked in settings but aren't played yet.

The harness is still there for trying things from a terminal:

```
dotnet run --project tools/RefereeHubApi.Harness
```

Type `help` in the harness for the command list. A typical match:

```
connect
make 75 0 TEST: (Team A) vs (Team B)
invite 12345
invite Some_Player
type tv
map 129891
mods HD,HR
info
timer 2m
start 10
...
log
close
```

Users can be given by id (`12345` or `#12345`) or by name (`Some_Player`, or `@12345` for a name made of digits).

## Settings and journal

Settings are plain JSON (no secrets) in `~/.config/RefereeHubApi/settings.json` on Linux: keybinds, macros, alert sounds, highlight keywords, chat log format and timer announcements. A macro is a list of command lines:

```json
{
  "macros": {
    "warmup": ["lock", "timer 3m", "say Warmup: 3 minutes"]
  }
}
```

Every step of a macro is checked before the first one runs, and a macro can't send private messages to more than one user.

Rooms that haven't ended are journaled under `~/.local/share/RefereeHubApi/journal` (snapshot without the password, chat, results). The journal is history only: nothing rejoins from it, `journal` in the harness lists entries and `join <room>` restores one.

## Tests

```
dotnet test
```

Set `REFEREEHUB_SNAPSHOTS=<dir>` to keep a PNG of each scene the desktop tests run. The keyring round-trip test only runs with `REFEREEHUB_TEST_KEYRING=1`, since a locked keyring may pop up an unlock prompt.

## Visual tests

```
dotnet run --project tools/RefereeHubApi.VisualTests
```

opens a window with every scene listed on the left, grouped by screen. A scene is the real app UI against an in-process fake hub and stubbed osu! API; its tests are listed as labels with their steps (hub events, clicks, checks) underneath, much like osu!'s test browser.

- Picking a scene runs every test, with a short delay between steps (adjustable). With Auto-run off only the first test's set-up runs.
- Click a label to reset and run just that test; click a step to run it once more on its own. Sliders are only moved by hand.
- The strip on each step shows its kind, then green when it passed or red when it failed, with the reason underneath.
- Ctrl+F searches (Enter opens the first match), Ctrl+R resets the scene, Ctrl+H hides the list.
- `-- --scene "Match flow"` opens a scene directly, otherwise the last one picked is reopened.

Scenes never touch your real settings, journal or keychain.

## Licence

The hub wire models in `src/RefereeHubApi/Hub/Models` are ported from osu-server-spectator (MIT).
