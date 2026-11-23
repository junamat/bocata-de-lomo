# Design notes

Notes on how the library is put together and on the hub/osu-web behaviour it depends on. Most of this came from reading the server code, since the referee hub is still marked as in active development and its docs are thin.

## Why no bot account, and what that means

Bot accounts need months of activity and public, documented tooling before support will grant one, so this tool doesn't rely on one. Without it:

- `client_credentials` + `delegate` (what the hub docs suggest for desktop clients) is rejected by osu-web.
- The `authorization_code` grant works, but only when the person logging in owns the OAuth app. So each ref registers their own app and logs in with it.
- A non-bot account can have at most 4 open rooms. `MakeRoomAsync` checks `ListRooms` first. It's not confirmed whether rooms you were only added to as a ref count towards that limit, so the check can be skipped.

Sharing one tournament account between refs was ruled out: it breaks osu!'s multi-account rules, and there'd be no way to tell which ref did what.

## Pieces

**`RefereeHubConnection`** is a thin typed wrapper over the SignalR connection to `https://spectator.osu.ppy.sh/referee`. Every hub method is there, and every hub event is an `IObservable<T>`. When the hub throws, the message contains `Error <n>: <text>`; that gets parsed into `RefereeHubException` with a code (1-21 at the time of writing). Errors without a code are opaque and stay that way.

**`RoomStateReducer` / `RoomSnapshot`.** Room state is an immutable snapshot, built from the `JoinRoom`/`MakeRoom` response and then updated only by hub events, never optimistically after sending a command. A few quirks:

- `UserJoined` doesn't say whether the user is a player or a ref, so it's decided by whether we know them as a ref (from the join response or `RefereeAdded`).
- The join response doesn't say which playlist item is current, so it's guessed (first unplayed) until the next `RoomSettingsChanged`.
- There's no "room closed" event. When a room is closed by anyone, or times out after 30 minutes empty, you get a `UserKicked` naming yourself, so that's what ends a room.

**`RefereeSession` / `RefereeRoom`** tie it together: resolve who we are (`/me`), connect, join rooms, open chat for each, and fetch scores when a map finishes.

**`ChatClient`** handles room chat (see below).

**`OsuApiClient`** handles the web API calls (chat, scores, `/me`). One instance per account; all requests share a 60/min limiter.

**`RefereeProfile`** holds the OAuth app and tokens and refreshes the access token a few minutes before it expires (access tokens last a day, refresh tokens three months).

**`ISecretStore`** is the keychain. Only the libsecret (Linux) implementation exists so far.

## Reconnecting

When the hub connection drops, the server removes the ref from all their rooms. They keep their ref rights, but have to `JoinRoom` again. SignalR's stateful reconnect can sometimes resume the same connection, in which case nothing was lost. So after an automatic reconnect we compare connection IDs, and only rejoin when it changed.

`Roll` and `SetLockState` fail (error 5) until the room is joined again, which is one more reason the rejoin happens automatically.

## Server restarts

When ppy deploys, the old server instance sends a `server_shutting_down` countdown and stays up for up to 6 hours. The hub docs recommend closing rooms, disconnecting and reconnecting as soon as possible, since invites can misbehave during the switch-over. The library only raises a warning. Closing rooms mid-match is the ref's call, and `ReconnectAsync(closeRoomsFirst: true)` does the rest when they're ready.

## Chat

The hub doesn't carry chat, only the channel ID, so chat goes through osu-web.

The notification websocket (`wss://notify.ppy.sh`) accepts third-party tokens with `chat.read`. But if the token isn't "verified" it silently drops chat events, with no error. To catch that:

- Websocket first. A slow check every 15 s fetches anything newer than the last message we've seen.
- If that check turns up messages older than a few seconds that the socket never delivered, the socket is considered broken for this session and we switch to polling every 3 s.
- If the socket just disconnects, we poll until it reconnects.

Both intervals are per request, round-robin across open channels, so 4 open rooms don't quadruple the request rate. With the 60/min budget that leaves room for sending messages and fetching scores.

## Scores

Lazer only ever wins on score, and the hub's `MatchCompleted` carries no results. After a map finishes we fetch `GET /rooms/{room}/playlist/{item}/scores` a few times (2, 5, 10, 20 s later) until everyone who started the map has a score. The result includes the teams and slots from when the map started, so totals per team can be worked out. Deciding who won the map or the match is left to the ref.

## Users and beatmaps

`UserDirectory` resolves names to ids (`GET /users/@name?key=username`) and keeps every user it has seen for the session, including chat senders and score owners. Spaces and underscores in names are treated as the same character, like osu! does. Ids are fetched in batches of 50, and a name or id that doesn't exist is remembered for a minute, so nothing gets requested more than once a minute (the API docs consider that abusive). Room actions take a `UserRef`, either an id or a name.

`BeatmapCatalog` does the same for beatmaps, plus star rating via `POST /beatmaps/{id}/attributes` cached per (beatmap, ruleset, mods). Star rating always comes from osu-web. The attributes endpoint doesn't return CS/AR/OD with mods, so `ModdedDifficulty` computes them the way lazer does for display: EZ/HR/DA first (HR caps at 10), then the rate change through approach time and the 300 window. After a rate change nothing is clamped, so DT can show AR 11 and HT a negative AR. Only the osu! ruleset gets CS/AR/OD adjustments; other rulesets only get BPM and length.

## Private messages

PMs use the same `ChatChannel` as room chat. The first message to someone goes through `POST /chat/new`, which also returns the channel; later ones go to that channel. The websocket delivers PMs even for channels we haven't opened, so an unknown channel there is treated as a new PM. In polling mode new PMs can't be discovered (the endpoint that would list them is lazer-only); only channels we already know are polled. The library never sends a PM on its own.

## Highlights, timer, log

`HighlightClassifier` is a pure function over a message: own name, "ref"/"referee" as a word in room chat, keywords, PMs. The session runs it on live messages only, never on the history loaded when a channel opens.

The hub has no generic timer, so `RefereeTimer` is local and has no effect on the room. It can post milestones to room chat if enabled.

`ChatTranscript` keeps a room's whole chat (the channel's replay buffer is bounded) and its results; `ExportLog` turns it into a plain-text log.

## Commands

`CommandTable` is the one parser for text commands, shared by the harness and a future UI. Arguments are validated when parsing, so a bad line fails before anything is sent, and unknown commands are errors rather than chat. Macros come from settings and are fully parsed before the first step runs.

## Journal

While a room is open its snapshot (minus the password), chat and results are saved to a JSON file, a couple of seconds after each change, and the file is removed when the room ends. If the client dies, the entries are left for the UI to offer. When a room is joined again its old chat and results are merged into the transcript, but the room state still comes from the join response and hub events.

## Things not verified yet

- Whether osu-web accepts a `http://localhost` callback for third-party OAuth apps (expected, not tried).
- Whether `JoinRoom` puts the ref in the room's chat channel. To be safe the client also joins it explicitly, which is why `chat.write_manage` is requested.
- The `slots`/`teams` dictionaries in `MatchStarted` are assumed to be keyed by user ID.
- Whether the attributes endpoint accepts lazer mod settings such as DT `speed_change` (not documented).
- Whether a `chat.message.new` for a channel we haven't opened is always a PM.
- Nothing has been run against production yet; the harness exists for that.
