using System.Reactive.Linq;
using System.Reactive.Subjects;
using RefereeHubApi.Api;
using RefereeHubApi.Chat;
using RefereeHubApi.Hub;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Hub.Models.Responses;
using RefereeHubApi.State;

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

        public Exception? ChatError { get; internal set; }

        public bool HasEnded => State.Ended != null;

        internal RefereeRoom(RefereeSession session, RoomSnapshot initial)
        {
            this.session = session;
            RoomId = initial.RoomId;
            state = new BehaviorSubject<RoomSnapshot>(initial);
        }

        internal (RoomSnapshot before, RoomSnapshot after) Apply(IRoomEvent e, int selfUserId, DateTimeOffset now)
        {
            RoomSnapshot before, after;

            lock (sync)
            {
                before = state.Value;
                after = RoomStateReducer.Apply(before, e, selfUserId, now);

                if (!ReferenceEquals(before, after))
                    state.OnNext(after);
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

        internal void PublishResult(MatchResult result) => results.OnNext(result);

        internal void Complete()
        {
            state.OnCompleted();
            events.OnCompleted();
            results.OnCompleted();
        }

        private RefereeHubConnection hub => session.Hub;

        #region Players

        public Task InviteAsync(int userId, CancellationToken ct = default) => hub.InvitePlayer(RoomId, userId, ct);

        public Task KickAsync(int userId, CancellationToken ct = default) => hub.KickPlayer(RoomId, userId, ct);

        public Task BanAsync(int userId, CancellationToken ct = default) => hub.BanUser(RoomId, userId, ct);

        public Task MoveAsync(int userId, byte? slot = null, MatchTeam? team = null, CancellationToken ct = default)
            => hub.MoveUser(RoomId, new MoveUserRequest { UserId = userId, Slot = slot, Team = team }, ct);

        public Task SetTeamAsync(int userId, MatchTeam team, CancellationToken ct = default) => MoveAsync(userId, team: team, ct: ct);

        public Task SetLockedAsync(bool locked, CancellationToken ct = default)
            => hub.SetLockState(RoomId, new SetLockStateRequest { Locked = locked }, ct);

        #endregion

        #region Referees

        public Task AddRefereeAsync(int userId, CancellationToken ct = default) => hub.AddReferee(RoomId, userId, ct);

        public Task RemoveRefereeAsync(int userId, CancellationToken ct = default) => hub.RemoveReferee(RoomId, userId, ct);

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

        public Task<IReadOnlyList<MultiplayerScore>> FetchScoresAsync(long playlistItemId, CancellationToken ct = default)
            => session.Api.GetPlaylistItemScoresAsync(RoomId, playlistItemId, ct);

        #endregion

        public Task CloseAsync(CancellationToken ct = default) => hub.CloseRoom(RoomId, ct);

        public Task<ChatMessage> SendChatAsync(string message, bool isAction = false, CancellationToken ct = default)
            => (Chat ?? throw new InvalidOperationException("Chat is not open for this room.", ChatError)).SendAsync(message, isAction, ct);
    }
}
