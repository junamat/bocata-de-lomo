using System.Reactive.Linq;
using System.Reactive.Subjects;
using RefereeHubApi.Api;
using RefereeHubApi.Beatmaps;
using RefereeHubApi.Chat;
using RefereeHubApi.Hub;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Hub.Models.Responses;
using RefereeHubApi.Logs;
using RefereeHubApi.Timers;
using RefereeHubApi.State;
using RefereeHubApi.Users;

namespace RefereeHubApi.Session
{
    // lazer always wins on score; deciding the map/match winner is left to the ref.
    public sealed record MatchResult(
        long RoomId,
        long PlaylistItemId,
        MatchInProgress? Match,
        IReadOnlyList<MultiplayerScore> Scores,
        int ExpectedScoreCount,
        bool Complete);

    public sealed class RefereeRoom
    {
        private readonly RefereeSession session;
        private readonly BehaviorSubject<RoomSnapshot> state;
        private readonly Subject<IRoomEvent> events = new Subject<IRoomEvent>();
        private readonly Subject<MatchResult> results = new Subject<MatchResult>();
        private readonly object sync = new object();

        public long RoomId { get; }

        public RoomSnapshot State => state.Value;

        public IObservable<RoomSnapshot> StateChanged => state.AsObservable();

        public IObservable<IRoomEvent> Events => events.AsObservable();

        public IObservable<MatchResult> Results => results.AsObservable();

        public ChatChannel? Chat { get; internal set; }

        // full chat history and map results for this room, for log export and the journal.
        public ChatTranscript Transcript { get; }

        // local only; never touches the hub.
        public RefereeTimer Timer { get; }

        private IDisposable? timerAnnouncer;

        internal Journal.RoomJournalRecorder? JournalRecorder { get; set; }

        public Exception? ChatError { get; internal set; }

        public bool HasEnded => State.Ended != null;

        internal RefereeRoom(RefereeSession session, RoomSnapshot initial, TimeProvider time)
        {
            var createdAt = time.GetUtcNow();
            Timer = new RefereeTimer(time);
            this.session = session;
            RoomId = initial.RoomId;
            state = new BehaviorSubject<RoomSnapshot>(initial);
            Transcript = new ChatTranscript(initial.ChatChannelId, $"#mp_{initial.RoomId} {initial.Name}", createdAt);
        }

        internal (RoomSnapshot before, RoomSnapshot after) Apply(IRoomEvent e, int selfUserId, DateTimeOffset now)
        {
            RoomSnapshot before, after;

            lock (sync)
            {
                before = state.Value;
                after = RoomStateReducer.Apply(before, e, selfUserId, now);

                if (!ReferenceEquals(before, after))
                {
                    state.OnNext(after);
                    Transcript.Title = $"#mp_{RoomId} {after.Name}";
                }
            }

            events.OnNext(e);
            return (before, after);
        }

        internal void Reset(RoomJoinedResponse response)
        {
            lock (sync)
                state.OnNext(RoomStateReducer.FromJoin(response));
        }

        internal void MarkEnded(RoomEndReason reason)
        {
            lock (sync)
            {
                if (state.Value.Ended == null)
                    state.OnNext(state.Value with { Ended = reason });
            }
        }

        internal void PublishResult(MatchResult result, DateTimeOffset at)
        {
            int? beatmapId = State.Playlist.FirstOrDefault(p => p.PlaylistItemId == result.PlaylistItemId)?.BeatmapId;
            Transcript.AddResult(new TranscriptResult(at, beatmapId, result));
            results.OnNext(result);
        }

        internal void Complete()
        {
            state.OnCompleted();
            events.OnCompleted();
            results.OnCompleted();
            Transcript.Complete();
            timerAnnouncer?.Dispose();
            Timer.Dispose();
        }

        private RefereeHubConnection hub => session.Hub;

        #region Players

        // users can be given by id or name; names are resolved through the session's user cache.
        private Task<int> resolve(UserRef user, CancellationToken ct) => session.Users.ResolveIdAsync(user, ct);

        public async Task InviteAsync(UserRef user, CancellationToken ct = default)
            => await hub.InvitePlayer(RoomId, await resolve(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        public async Task KickAsync(UserRef user, CancellationToken ct = default)
            => await hub.KickPlayer(RoomId, await resolve(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        public async Task BanAsync(UserRef user, CancellationToken ct = default)
            => await hub.BanUser(RoomId, await resolve(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        public async Task MoveAsync(UserRef user, byte? slot = null, MatchTeam? team = null, CancellationToken ct = default)
        {
            int userId = await resolve(user, ct).ConfigureAwait(false);
            await hub.MoveUser(RoomId, new MoveUserRequest { UserId = userId, Slot = slot, Team = team }, ct).ConfigureAwait(false);
        }

        public Task SetTeamAsync(UserRef user, MatchTeam team, CancellationToken ct = default) => MoveAsync(user, team: team, ct: ct);

        public Task SetLockedAsync(bool locked, CancellationToken ct = default)
            => hub.SetLockState(RoomId, new SetLockStateRequest { Locked = locked }, ct);

        #endregion

        #region Referees

        public async Task AddRefereeAsync(UserRef user, CancellationToken ct = default)
            => await hub.AddReferee(RoomId, await resolve(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        public async Task RemoveRefereeAsync(UserRef user, CancellationToken ct = default)
            => await hub.RemoveReferee(RoomId, await resolve(user, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        #endregion

        #region Settings

        public Task ChangeSettingsAsync(ChangeRoomSettingsRequest request, CancellationToken ct = default)
            => hub.ChangeRoomSettings(RoomId, request, ct);

        public Task SetNameAsync(string name, CancellationToken ct = default)
            => ChangeSettingsAsync(new ChangeRoomSettingsRequest { Name = name }, ct);

        public Task SetPasswordAsync(string password, CancellationToken ct = default)
            => ChangeSettingsAsync(new ChangeRoomSettingsRequest { Password = password }, ct);

        public Task SetMatchTypeAsync(MatchType type, CancellationToken ct = default)
            => ChangeSettingsAsync(new ChangeRoomSettingsRequest { MatchType = type }, ct);

        public Task SetQueueModeAsync(QueueMode mode, CancellationToken ct = default)
            => ChangeSettingsAsync(new ChangeRoomSettingsRequest { QueueMode = mode }, ct);

        public Task SetSizeAsync(byte size, CancellationToken ct = default)
            => ChangeSettingsAsync(new ChangeRoomSettingsRequest { MaxParticipants = size }, ct);

        #endregion

        #region Playlist

        // changing ruleset without passing mods clears the mods (hub behaviour).
        public Task SetMapAsync(int beatmapId, int? rulesetId = null, CancellationToken ct = default)
            => hub.EditCurrentPlaylistItem(RoomId, new EditCurrentPlaylistItemRequest { BeatmapId = beatmapId, RulesetId = rulesetId }, ct);

        // allowed mods and freestyle can't be combined (error 13).
        public Task SetModsAsync(IEnumerable<Mod> requiredMods, IEnumerable<Mod>? allowedMods = null, bool? freestyle = null, CancellationToken ct = default)
            => hub.EditCurrentPlaylistItem(RoomId, new EditCurrentPlaylistItemRequest
            {
                RequiredMods = requiredMods.ToArray(),
                AllowedMods = allowedMods?.ToArray() ?? (freestyle == true ? [] : null),
                Freestyle = freestyle,
            }, ct);

        public Task EditCurrentItemAsync(EditCurrentPlaylistItemRequest request, CancellationToken ct = default)
            => hub.EditCurrentPlaylistItem(RoomId, request, ct);

        public Task AddPlaylistItemAsync(AddPlaylistItemRequest request, CancellationToken ct = default)
            => hub.AddPlaylistItem(RoomId, request, ct);

        public Task EditPlaylistItemAsync(EditPlaylistItemRequest request, CancellationToken ct = default)
            => hub.EditPlaylistItem(RoomId, request, ct);

        public Task RemovePlaylistItemAsync(long playlistItemId, CancellationToken ct = default)
            => hub.RemovePlaylistItem(RoomId, new RemovePlaylistItemRequest { PlaylistItemId = playlistItemId }, ct);

        #endregion

        #region Match

        public Task StartAsync(int? countdownSeconds = null, CancellationToken ct = default)
            => hub.StartMatch(RoomId, new StartGameplayRequest { Countdown = countdownSeconds }, ct);

        public Task StopCountdownAsync(CancellationToken ct = default) => hub.StopMatchCountdown(RoomId, ct);

        public Task AbortAsync(CancellationToken ct = default) => hub.AbortMatch(RoomId, ct);

        public Task RollAsync(uint? max = null, CancellationToken ct = default)
            => hub.Roll(RoomId, max == null ? null : new RollRequest { Max = max }, ct);

        // metadata, star rating with the item's required mods, and mod-adjusted CS/AR/OD. Defaults to the current item.
        public Task<BeatmapInfo> DescribeBeatmapAsync(PlaylistItem? item = null, CancellationToken ct = default)
        {
            item ??= State.CurrentPlaylistItem ?? throw new InvalidOperationException("The current playlist item isn't known yet.");
            return session.Beatmaps.DescribeAsync(item, ct);
        }

        public Task<IReadOnlyList<MultiplayerScore>> FetchScoresAsync(long playlistItemId, CancellationToken ct = default)
            => session.Api.GetPlaylistItemScoresAsync(RoomId, playlistItemId, ct);

        #endregion

        public Task CloseAsync(CancellationToken ct = default) => hub.CloseRoom(RoomId, ct);

        // starts (or restarts) the room's timer; with announcement enabled, milestones are posted to room chat.
        public void StartTimer(TimeSpan duration, TimerAnnouncement? announcement = null)
        {
            announcement ??= new TimerAnnouncement();
            timerAnnouncer?.Dispose();
            timerAnnouncer = Chat == null
                ? null
                : TimerAnnouncer.Attach(Timer, announcement, text => SendChatAsync(text), session.ReportBackgroundError);

            Timer.Start(duration, announcement.MilestoneSeconds.Select(sec => TimeSpan.FromSeconds(sec)));

            if (announcement is { Enabled: true, AnnounceStart: true } && Chat != null)
                _ = announceStart(string.Format(announcement.StartedMessage, RefereeTimer.FormatRemaining(duration)));
        }

        public void StopTimer() => Timer.Stop();

        private async Task announceStart(string text)
        {
            try
            {
                await SendChatAsync(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                session.ReportBackgroundError(ex);
            }
        }

        public string ExportLog(ChatLogFormat? format = null)
            => ChatLogFormatter.Format(Transcript, format, session.Users.GetCachedUsername);

        public Task<ChatMessage> SendChatAsync(string message, bool isAction = false, CancellationToken ct = default)
            => (Chat ?? throw new InvalidOperationException("Chat is not open for this room.", ChatError)).SendAsync(message, isAction, ct);
    }
}
