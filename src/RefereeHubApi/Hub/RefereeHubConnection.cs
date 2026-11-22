using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Hub.Models.Responses;

namespace RefereeHubApi.Hub
{
    public enum HubConnectionStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
    }

    public class RefereeHubConnectionOptions
    {
        public static readonly Uri PRODUCTION_URL = new Uri("https://spectator.osu.ppy.sh/referee");

        public Uri HubUrl { get; set; } = PRODUCTION_URL;

        public Action<HttpConnectionOptions>? ConfigureHttpConnection { get; set; }

        public bool UseStatefulReconnect { get; set; } = true;

        public TimeSpan[] ReconnectDelays { get; set; } =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
        ];
    }

    public sealed class RefereeHubConnection : IAsyncDisposable
    {
        private readonly HubConnection connection;
        private readonly ILogger logger;
        private readonly BehaviorSubject<HubConnectionStatus> status = new BehaviorSubject<HubConnectionStatus>(HubConnectionStatus.Disconnected);
        private readonly Subject<string?> reconnected = new Subject<string?>();
        private readonly Subject<Exception?> closed = new Subject<Exception?>();
        private readonly Subject<IRoomEvent> events = new Subject<IRoomEvent>();
        private readonly List<IDisposable> subjects = new List<IDisposable>();

        public RefereeHubConnection(RefereeHubConnectionOptions options, Func<CancellationToken, Task<string>> accessTokenProvider, ILogger? logger = null)
        {
            this.logger = logger ?? NullLogger.Instance;

            var builder = new HubConnectionBuilder()
                          .WithUrl(options.HubUrl, http =>
                          {
                              http.AccessTokenProvider = async () => await accessTokenProvider(CancellationToken.None).ConfigureAwait(false);
                              options.ConfigureHttpConnection?.Invoke(http);
                          })
                          .WithAutomaticReconnect(new ForeverRetryPolicy(options.ReconnectDelays));

            if (options.UseStatefulReconnect)
                builder = builder.WithStatefulReconnect();

            connection = builder.Build();

            connection.Reconnecting += ex =>
            {
                this.logger.LogWarning(ex, "Referee hub connection lost, reconnecting");
                status.OnNext(HubConnectionStatus.Reconnecting);
                return Task.CompletedTask;
            };
            connection.Reconnected += id =>
            {
                this.logger.LogInformation("Referee hub reconnected (connection {ConnectionId})", id);
                status.OnNext(HubConnectionStatus.Connected);
                reconnected.OnNext(id);
                return Task.CompletedTask;
            };
            connection.Closed += ex =>
            {
                if (ex != null)
                    this.logger.LogError(ex, "Referee hub connection closed");
                status.OnNext(HubConnectionStatus.Disconnected);
                closed.OnNext(ex);
                return Task.CompletedTask;
            };

            UserJoined = on<UserJoinedEvent>("UserJoined");
            UserLeft = on<UserLeftEvent>("UserLeft");
            UserKicked = on<UserKickedEvent>("UserKicked");
            UserBanned = on<UserBannedEvent>("UserBanned");
            RefereeAdded = on<RefereeAddedEvent>("RefereeAdded");
            RefereeRemoved = on<RefereeRemovedEvent>("RefereeRemoved");
            RefereeInvited = on<RefereeInvitedEvent>("RefereeInvited");
            RoomSettingsChanged = on<RoomSettingsChangedEvent>("RoomSettingsChanged");
            MatchStateChanged = on<MatchStateChangedEvent>("MatchStateChanged");
            PlaylistItemAdded = on<PlaylistItemAddedEvent>("PlaylistItemAdded");
            PlaylistItemChanged = on<PlaylistItemChangedEvent>("PlaylistItemChanged");
            PlaylistItemRemoved = on<PlaylistItemRemovedEvent>("PlaylistItemRemoved");
            RollCompleted = on<RollCompletedEvent>("RollCompleted");
            UserStatusChanged = on<UserStatusChangedEvent>("UserStatusChanged");
            UserModsChanged = on<UserModsChangedEvent>("UserModsChanged");
            UserStyleChanged = on<UserStyleChangedEvent>("UserStyleChanged");
            UserTeamChanged = on<UserTeamChangedEvent>("UserTeamChanged");
            CountdownStarted = on<CountdownStartedEvent>("CountdownStarted");
            CountdownStopped = on<CountdownStoppedEvent>("CountdownStopped");
            MatchStarted = on<MatchStartedEvent>("MatchStarted");
            MatchAborted = on<MatchAbortedEvent>("MatchAborted");
            MatchCompleted = on<MatchCompletedEvent>("MatchCompleted");
        }

        #region Connection

        public IObservable<HubConnectionStatus> Status => status.AsObservable();

        public HubConnectionStatus CurrentStatus => status.Value;

        public string? ConnectionId => connection.ConnectionId;

        // a different connection id than before means the server dropped us from every room we had joined.
        public IObservable<string?> Reconnected => reconnected.AsObservable();

        public IObservable<Exception?> Closed => closed.AsObservable();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            status.OnNext(HubConnectionStatus.Connecting);

            try
            {
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                status.OnNext(HubConnectionStatus.Connected);
            }
            catch
            {
                status.OnNext(HubConnectionStatus.Disconnected);
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => connection.StopAsync(cancellationToken);

        #endregion

        #region Events (IRefereeHubClient)

        public IObservable<IRoomEvent> Events => events.AsObservable();

        public IObservable<UserJoinedEvent> UserJoined { get; }
        public IObservable<UserLeftEvent> UserLeft { get; }
        public IObservable<UserKickedEvent> UserKicked { get; }
        public IObservable<UserBannedEvent> UserBanned { get; }
        public IObservable<RefereeAddedEvent> RefereeAdded { get; }
        public IObservable<RefereeRemovedEvent> RefereeRemoved { get; }
        public IObservable<RefereeInvitedEvent> RefereeInvited { get; }
        public IObservable<RoomSettingsChangedEvent> RoomSettingsChanged { get; }
        public IObservable<MatchStateChangedEvent> MatchStateChanged { get; }
        public IObservable<PlaylistItemAddedEvent> PlaylistItemAdded { get; }
        public IObservable<PlaylistItemChangedEvent> PlaylistItemChanged { get; }
        public IObservable<PlaylistItemRemovedEvent> PlaylistItemRemoved { get; }
        public IObservable<RollCompletedEvent> RollCompleted { get; }
        public IObservable<UserStatusChangedEvent> UserStatusChanged { get; }
        public IObservable<UserModsChangedEvent> UserModsChanged { get; }
        public IObservable<UserStyleChangedEvent> UserStyleChanged { get; }
        public IObservable<UserTeamChangedEvent> UserTeamChanged { get; }
        public IObservable<CountdownStartedEvent> CountdownStarted { get; }
        public IObservable<CountdownStoppedEvent> CountdownStopped { get; }
        public IObservable<MatchStartedEvent> MatchStarted { get; }
        public IObservable<MatchAbortedEvent> MatchAborted { get; }
        public IObservable<MatchCompletedEvent> MatchCompleted { get; }

        private IObservable<T> on<T>(string methodName)
            where T : IRoomEvent
        {
            var subject = new Subject<T>();
            subjects.Add(subject);

            connection.On<T>(methodName, e =>
            {
                logger.LogDebug("Hub event {Event} for room {RoomId}", methodName, e.RoomId);
                events.OnNext(e);
                subject.OnNext(e);
            });

            return subject.AsObservable();
        }

        #endregion

        #region Methods (IRefereeHubServer)

        public Task<RoomJoinedResponse> MakeRoom(MakeRoomRequest request, CancellationToken ct = default)
            => invoke<RoomJoinedResponse>(nameof(MakeRoom), ct, request);

        public Task<RoomJoinedResponse> JoinRoom(long roomId, CancellationToken ct = default)
            => invoke<RoomJoinedResponse>(nameof(JoinRoom), ct, roomId);

        public Task CloseRoom(long roomId, CancellationToken ct = default)
            => invoke(nameof(CloseRoom), ct, roomId);

        // fails with error 19 unless the player is online in multiplayer.
        public Task InvitePlayer(long roomId, int userId, CancellationToken ct = default)
            => invoke(nameof(InvitePlayer), ct, roomId, userId);

        public Task KickPlayer(long roomId, int userId, CancellationToken ct = default)
            => invoke(nameof(KickPlayer), ct, roomId, userId);

        public Task BanUser(long roomId, int bannedUserId, CancellationToken ct = default)
            => invoke(nameof(BanUser), ct, roomId, bannedUserId);

        // AddReferee/RemoveReferee only work for the room host.
        public Task AddReferee(long roomId, int targetUserId, CancellationToken ct = default)
            => invoke(nameof(AddReferee), ct, roomId, targetUserId);

        public Task RemoveReferee(long roomId, int targetUserId, CancellationToken ct = default)
            => invoke(nameof(RemoveReferee), ct, roomId, targetUserId);

        public Task<ListRoomsResponse> ListRooms(CancellationToken ct = default)
            => invoke<ListRoomsResponse>(nameof(ListRooms), ct);

        public Task ChangeRoomSettings(long roomId, ChangeRoomSettingsRequest request, CancellationToken ct = default)
            => invoke(nameof(ChangeRoomSettings), ct, roomId, request);

        public Task EditCurrentPlaylistItem(long roomId, EditCurrentPlaylistItemRequest request, CancellationToken ct = default)
            => invoke(nameof(EditCurrentPlaylistItem), ct, roomId, request);

        public Task AddPlaylistItem(long roomId, AddPlaylistItemRequest request, CancellationToken ct = default)
            => invoke(nameof(AddPlaylistItem), ct, roomId, request);

        public Task EditPlaylistItem(long roomId, EditPlaylistItemRequest request, CancellationToken ct = default)
            => invoke(nameof(EditPlaylistItem), ct, roomId, request);

        public Task RemovePlaylistItem(long roomId, RemovePlaylistItemRequest request, CancellationToken ct = default)
            => invoke(nameof(RemovePlaylistItem), ct, roomId, request);

        // Roll and SetLockState need this connection to have joined the room.
        public Task Roll(long roomId, RollRequest? request = null, CancellationToken ct = default)
            => invoke(nameof(Roll), ct, roomId, request);

        public Task MoveUser(long roomId, MoveUserRequest request, CancellationToken ct = default)
            => invoke(nameof(MoveUser), ct, roomId, request);

        public Task SetLockState(long roomId, SetLockStateRequest request, CancellationToken ct = default)
            => invoke(nameof(SetLockState), ct, roomId, request);

        public Task StartMatch(long roomId, StartGameplayRequest request, CancellationToken ct = default)
            => invoke(nameof(StartMatch), ct, roomId, request);

        public Task StopMatchCountdown(long roomId, CancellationToken ct = default)
            => invoke(nameof(StopMatchCountdown), ct, roomId);

        public Task AbortMatch(long roomId, CancellationToken ct = default)
            => invoke(nameof(AbortMatch), ct, roomId);

        private async Task invoke(string methodName, CancellationToken ct, params object?[] args)
            => await invokeCore<object>(methodName, ct, args, returnsValue: false).ConfigureAwait(false);

        private Task<T> invoke<T>(string methodName, CancellationToken ct, params object?[] args)
            => invokeCore<T>(methodName, ct, args, returnsValue: true);

        private async Task<T> invokeCore<T>(string methodName, CancellationToken ct, object?[] args, bool returnsValue)
        {
            logger.LogDebug("Invoking {Method}", methodName);

            try
            {
                if (!returnsValue)
                {
                    await connection.InvokeCoreAsync(methodName, args, ct).ConfigureAwait(false);
                    return default!;
                }

                object? result = await connection.InvokeCoreAsync(methodName, typeof(T), args, ct).ConfigureAwait(false);
                return (T)result!;
            }
            catch (HubException ex)
            {
                var mapped = RefereeHubException.FromHubException(methodName, ex);
                logger.LogWarning("{Method} failed: {Code} {Message}", methodName, mapped.RawCode, mapped.HubMessage);
                throw mapped;
            }
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            foreach (var s in subjects)
                s.Dispose();

            events.Dispose();
            reconnected.Dispose();
            closed.Dispose();
            status.Dispose();
        }

        private class ForeverRetryPolicy : IRetryPolicy
        {
            private readonly TimeSpan[] delays;

            public ForeverRetryPolicy(TimeSpan[] delays)
            {
                this.delays = delays.Length > 0 ? delays : [TimeSpan.FromSeconds(5)];
            }

            public TimeSpan? NextRetryDelay(RetryContext retryContext)
                => delays[Math.Min(retryContext.PreviousRetryCount, delays.Length - 1)];
        }
    }
}
