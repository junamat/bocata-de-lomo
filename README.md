# RefereeHubApi

Referee tooling for osu! tournaments played in lazer multiplayer. It talks to the referee hub in
[osu-server-spectator](https://github.com/ppy/osu-server-spectator), which replaces the old `!mp` bancho commands for lazer multiplayer.

This repo is the client-side library: everything a ref needs to run a match (make the room, invite players, set maps and mods, start, read chat, get scores, close). There's no UI yet; the library is meant to be linked directly by one later.

## Layout

- `src/RefereeHubApi` - the library
- `tools/RefereeHubApi.Harness` - a small console for trying things against the real servers
- `tests/RefereeHubApi.Tests` - unit tests
- `tests/RefereeHubApi.HubTests` - tests against an in-process fake hub (needs the ASP.NET Core runtime)

## Requirements

- .NET 10 SDK
- Linux for now: tokens are stored with libsecret (GNOME Keyring / KWallet). Windows and macOS storage isn't written yet.
- An osu! OAuth application of your own (see below)

## Setting up as a ref

Every ref uses their own osu! account and their own OAuth app. Without a bot account, osu-web only issues the scopes the hub needs to the owner of the OAuth app, so sharing one app between refs doesn't work.

1. Go to your osu! account settings > OAuth > New OAuth Application.
2. Set the callback URL to `http://localhost:7270/callback` (the port can be changed, but it must match exactly in both places).
3. Run the harness, `setup` with the client ID and secret, then `login`.

The client secret and tokens are stored in your OS keychain, nowhere else.

```
dotnet run --project tools/RefereeHubApi.Harness
```

Type `help` in the harness for the command list. A typical match:

```
connect
make 75 0 TEST: (Team A) vs (Team B)
invite 12345
type tv
map 129891
mods HD,HR
start 10
...
close
```

## Tests

```
dotnet test
```

The keyring round-trip test only runs with `REFEREEHUB_TEST_KEYRING=1`, since a locked keyring may pop up an unlock prompt.

## Licence

The hub wire models in `src/RefereeHubApi/Hub/Models` are ported from osu-server-spectator (MIT).
