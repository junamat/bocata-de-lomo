using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RefereeHubApi.Api;
using RefereeHubApi.Auth;
using RefereeHubApi.Chat;
using RefereeHubApi.Hub;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Hub.Models.Responses;
using RefereeHubApi.State;

namespace RefereeHubApi.Session
{
    public sealed class RefereeSessionOptions
    {
        public RefereeHubConnectionOptions Hub { get; set; } = new RefereeHubConnectionOptions();

        public Uri ApiBaseUrl { get; set; } = OsuApiClient.DEFAULT_BASE_URL;

        public int ApiRequestsPerMinute { get; set; } = OsuApiClient.DEFAULT_REQUESTS_PER_MINUTE;

        public bool EnableChat { get; set; } = true;

        public ChatClientOptions Chat { get; set; } = new ChatClientOptions();

        public int RoomLimit { get; set; } = 4;

        // includes rooms another ref added us to but we never joined.
        public bool JoinListedRoomsOnStart { get; set; } = true;

        public TimeSpan[] ScoreFetchDelays { get; set; } =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
        ];
    }

    // the old instance stays up for up to 6 hours, but things like invites may misbehave until we move over.
    public sealed record ServerShutdownWarning(long RoomId, TimeSpan TimeRemaining, DateTimeOffset ReceivedAt);

    public class RoomLimitReachedException : Exception
    {
        public IReadOnlyList<long> OpenRoomIds { get; }

        public RoomLimitReachedException(IReadOnlyList<long> openRoomIds, int limit)
            : base($"Already referee of {openRoomIds.Count} room(s); the limit is {limit}. Close one first.")
        {
            OpenRoomIds = openRoomIds;
        }
    }

    public sealed class RefereeSession : IAsyncDisposable
    {
        private readonly RefereeSessionOptions options;
        private readonly TimeProvider time;
        private readonly ILogger logger;
        private readonly bool ownsHttp;
        private readonly HttpClient http;
        private readonly ConcurrentDictionary<long, RefereeRoom> rooms = new ConcurrentDictionary<long, RefereeRoom>();
        private readonly Subject<RefereeRoom> roomAdded = new Subject<RefereeRoom>();
        private readonly Subject<RefereeRoom> roomRemoved = new Subject<RefereeRoom>();
        private readonly Subject<RefereeInvitedEvent> invitations = new Subject<RefereeInvitedEvent>();
        private readonly Subject<ServerShutdownWarning> shutdownWarnings = new Subject<ServerShutdownWarning>();
        private readonly Subject<Exception> backgroundErrors = new Subject<Exception>();
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly SemaphoreSlim joinLock = new SemaphoreSlim(1, 1);

        // events for rooms not registered yet, kept while a MakeRoom/JoinRoom call is in flight (see routeEvent).
        private readonly List<IRoomEvent> pendingEvents = new List<IRoomEvent>();
        private int roomCallsInFlight;
        private string? lastConnectionId;
        private ChatClient? chat;

        public ApiUser Self { get; private set; } = null!;

        public RefereeHubConnection Hub { get; }

        public OsuApiClient Api { get; }

        public ChatClient? Chat => chat;

        public IReadOnlyCollection<RefereeRoom> Rooms => rooms.Values.ToArray();

        public IObservable<RefereeRoom> RoomAdded => roomAdded.AsObservable();

        public IObservable<RefereeRoom> RoomRemoved => roomRemoved.AsObservable();

        public IObservable<RefereeInvitedEvent> Invitations => invitations.AsObservable();

        public IObservable<ServerShutdownWarning> ServerShuttingDown => shutdownWarnings.AsObservable();

        public IObservable<HubConnectionStatus> ConnectionStatus => Hub.Status;

        public IObservable<Exception> BackgroundErrors => backgroundErrors.AsObservable();

        private RefereeSession(IRefereeCredentials credentials, RefereeSessionOptions options, HttpClient? http, TimeProvider time, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.time = time;
            logger = loggerFactory.CreateLogger<RefereeSession>();
            ownsHttp = http == null;
            this.http = http ?? new HttpClient();

            Api = new OsuApiClient(this.http, credentials, options.ApiBaseUrl, options.ApiRequestsPerMinute, loggerFactory.CreateLogger<OsuApiClient>());
            Hub = new RefereeHubConnection(options.Hub, credentials.GetAccessTokenAsync, loggerFactory.CreateLogger<RefereeHubConnection>());

            subscriptions.Add(Hub.Events.Subscribe(routeEvent));
            subscriptions.Add(Hub.Reconnected.Subscribe(id => _ = onReconnected(id)));
        }

        public static async Task<RefereeSession> StartAsync(
            IRefereeCredentials credentials,
            RefereeSessionOptions? options = null,
            HttpClient? http = null,
            TimeProvider? time = null,
            ILoggerFactory? loggerFactory = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new RefereeSessionOptions();
            loggerFactory ??= NullLoggerFactory.Instance;

            var session = new RefereeSession(credentials, options, http, time ?? TimeProvider.System, loggerFactory);

            try
            {
                session.Self = await session.Api.GetMeAsync(cancellationToken).ConfigureAwait(false);

                if (options.EnableChat)
                    session.chat = new ChatClient(session.Api, credentials, session.Self.Id, options.Chat, session.time, loggerFactory.CreateLogger<ChatClient>());

                await session.Hub.StartAsync(cancellationToken).ConfigureAwait(false);
                session.lastConnectionId = session.Hub.ConnectionId;

                if (options.JoinListedRoomsOnStart)
                {
                    foreach (long roomId in await session.ListRoomsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        try
                        {
                            await session.JoinRoomAsync(roomId, cancellationToken).ConfigureAwait(false);
                        }
                        catch (RefereeHubException ex)
                        {
                            session.logger.LogWarning("Could not rejoin room {RoomId}: {Message}", roomId, ex.Message);
                            session.backgroundErrors.OnNext(ex);
                        }
                    }
                }

                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public RefereeRoom? GetRoom(long roomId) => rooms.GetValueOrDefault(roomId);

        public async Task<IReadOnlyList<long>> ListRoomsAsync(CancellationToken ct = default)
            => (await Hub.ListRooms(ct).ConfigureAwait(false)).RoomIds;

        // checks the 4-room limit first. unverified whether rooms we were only added to count towards it, hence ignoreRoomLimit.
        public async Task<RefereeRoom> MakeRoomAsync(MakeRoomRequest request, bool ignoreRoomLimit = false, CancellationToken ct = default)
        {
            if (!ignoreRoomLimit)
            {
                var open = await ListRoomsAsync(ct).ConfigureAwait(false);
                if (open.Count >= options.RoomLimit)
                    throw new RoomLimitReachedException(open, options.RoomLimit);
            }

            return await joinOrMake(() => Hub.MakeRoom(request, ct), ct).ConfigureAwait(false);
        }

        public async Task<RefereeRoom> JoinRoomAsync(long roomId, CancellationToken ct = default)
        {
            return await joinOrMake(() => Hub.JoinRoom(roomId, ct), ct).ConfigureAwait(false);
        }

        // for moving to a new server instance after a shutdown warning. closeRoomsFirst kicks everyone out of our rooms.
        public async Task ReconnectAsync(bool closeRoomsFirst = false, CancellationToken ct = default)
        {
            if (closeRoomsFirst)
            {
                foreach (var room in Rooms)
                {
                    try
                    {
                        await room.CloseAsync(ct).ConfigureAwait(false);
                    }
                    catch (RefereeHubException ex)
                    {
                        logger.LogWarning("Closing room {RoomId} failed: {Message}", room.RoomId, ex.Message);
                    }

                    removeRoom(room, RoomEndReason.ClosedOrKicked);
                }
            }

            await Hub.StopAsync(ct).ConfigureAwait(false);
            await Hub.StartAsync(ct).ConfigureAwait(false);
            lastConnectionId = Hub.ConnectionId;
            await rejoinTrackedRooms(ct).ConfigureAwait(false);
        }

        // events for the new room can arrive before the invocation result does; they're buffered (see routeEvent)
        // until the room is registered, so the buffer must outlive registerRoom.
        private async Task<RefereeRoom> joinOrMake(Func<Task<RoomJoinedResponse>> call, CancellationToken ct)
        {
            Interlocked.Increment(ref roomCallsInFlight);

            try
            {
                var response = await call().ConfigureAwait(false);
                return await registerRoom(response, ct).ConfigureAwait(false);
            }
            finally
            {
                if (Interlocked.Decrement(ref roomCallsInFlight) == 0)
                {
                    lock (pendingEvents)
                        pendingEvents.Clear();
                }
            }
        }

        private async Task<RefereeRoom> registerRoom(RoomJoinedResponse response, CancellationToken ct)
        {
            RefereeRoom room;
            bool added = false;

            lock (rooms)
            {
                if (rooms.TryGetValue(response.RoomId, out var existing))
                {
                    existing.Reset(response);
                    room = existing;
                }
                else
                {
                    room = new RefereeRoom(this, RoomStateReducer.FromJoin(response));
                    rooms[response.RoomId] = room;
                    added = true;
                }

                // events that raced the invocation result. Hub state changes are mostly "set" operations, so replaying
                // them onto the fresh snapshot is the lesser risk compared to dropping them.
                IRoomEvent[] raced;

                lock (pendingEvents)
                {
                    raced = pendingEvents.Where(e => e.RoomId == response.RoomId).ToArray();
                    pendingEvents.RemoveAll(e => e.RoomId == response.RoomId);
                }

                foreach (var e in raced)
                    applyToRoom(room, e);
            }

            if (added)
            {
                roomAdded.OnNext(room);

                if (chat != null && room.Chat == null)
                {
                    try
                    {
                        room.Chat = await chat.OpenChannelAsync(response.ChatChannelId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning("Could not open chat for room {RoomId}: {Message}", response.RoomId, ex.Message);
                        room.ChatError = ex;
                        backgroundErrors.OnNext(ex);
                    }
                }
            }

            return room;
        }

        private void routeEvent(IRoomEvent e)
        {
            if (e is RefereeInvitedEvent invited)
            {
                invitations.OnNext(invited);
                return;
            }

            if (e is CountdownStartedEvent { Type: CountdownType.ServerShuttingDown } shutdown)
            {
                logger.LogWarning("Server shutting down (room {RoomId}, {Seconds}s)", shutdown.RoomId, shutdown.Seconds);
                shutdownWarnings.OnNext(new ServerShutdownWarning(shutdown.RoomId, TimeSpan.FromSeconds(shutdown.Seconds), time.GetUtcNow()));
            }

            RefereeRoom? room;

            lock (rooms)
            {
                if (!rooms.TryGetValue(e.RoomId, out room))
                {
                    if (Volatile.Read(ref roomCallsInFlight) > 0)
                    {
                        lock (pendingEvents)
                            pendingEvents.Add(e);
                    }

                    return;
                }

                applyToRoom(room, e);
            }
        }

        private void applyToRoom(RefereeRoom room, IRoomEvent e)
        {
            var (before, after) = room.Apply(e, Self.Id, time.GetUtcNow());

            if (e is MatchCompletedEvent completed)
                _ = fetchResults(room, completed.PlaylistItemId, before);

            if (after.Ended is RoomEndReason reason && before.Ended == null)
                removeRoom(room, reason);
        }

        private void removeRoom(RefereeRoom room, RoomEndReason reason)
        {
            if (!rooms.TryRemove(room.RoomId, out _))
                return;

            room.MarkEnded(reason);

            if (room.Chat != null)
                chat?.CloseChannel(room.Chat.ChannelId);

            roomRemoved.OnNext(room);
            room.Complete();
        }

        private async Task fetchResults(RefereeRoom room, long playlistItemId, RoomSnapshot beforeCompletion)
        {
            var match = beforeCompletion.Match?.PlaylistItemId == playlistItemId ? beforeCompletion.Match : null;

            int expected = match is { Slots.Count: > 0 }
                ? match.Slots.Count
                : beforeCompletion.Players.Values.Count(p => p.Status is MatchUserStatus.Playing or MatchUserStatus.FinishedPlay);

            IReadOnlyList<MultiplayerScore> scores = [];

            try
            {
                foreach (var delay in options.ScoreFetchDelays)
                {
                    await Task.Delay(delay, time, cts.Token).ConfigureAwait(false);
                    scores = await Api.GetPlaylistItemScoresAsync(room.RoomId, playlistItemId, cts.Token).ConfigureAwait(false);

                    if (scores.Count >= expected)
                        break;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Score fetch for room {RoomId} item {Item} failed: {Message}", room.RoomId, playlistItemId, ex.Message);
                backgroundErrors.OnNext(ex);
            }

            room.PublishResult(new MatchResult(room.RoomId, playlistItemId, match, scores, expected, scores.Count >= expected));
        }

        private async Task onReconnected(string? connectionId)
        {
            // a stateful reconnect keeps the connection (and the server-side room joins); a new ID means we were dropped from all rooms.
            if (connectionId == lastConnectionId)
                return;

            lastConnectionId = connectionId;

            try
            {
                await rejoinTrackedRooms(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Rejoining rooms after reconnect failed");
                backgroundErrors.OnNext(ex);
            }
        }

        private async Task rejoinTrackedRooms(CancellationToken ct)
        {
            await joinLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var listed = (await ListRoomsAsync(ct).ConfigureAwait(false)).ToHashSet();

                foreach (var room in Rooms)
                {
                    if (!listed.Contains(room.RoomId))
                    {
                        removeRoom(room, RoomEndReason.ClosedOrKicked);
                        continue;
                    }

                    try
                    {
                        await JoinRoomAsync(room.RoomId, ct).ConfigureAwait(false);
                    }
                    catch (RefereeHubException ex) when (ex.Code is RefereeHubErrorCode.RoomDoesNotExist or RefereeHubErrorCode.UserNotReferee or RefereeHubErrorCode.RoomNotJoinable)
                    {
                        removeRoom(room, ex.Code == RefereeHubErrorCode.UserNotReferee ? RoomEndReason.RefereeRemoved : RoomEndReason.ClosedOrKicked);
                    }
                }
            }
            finally
            {
                joinLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync().ConfigureAwait(false);

            foreach (var s in subscriptions)
                s.Dispose();

            if (chat != null)
                await chat.DisposeAsync().ConfigureAwait(false);

            await Hub.DisposeAsync().ConfigureAwait(false);
            Api.Dispose();

            if (ownsHttp)
                http.Dispose();

            foreach (var room in rooms.Values)
                room.Complete();

            rooms.Clear();
            roomAdded.OnCompleted();
            roomRemoved.OnCompleted();
            invitations.OnCompleted();
            shutdownWarnings.OnCompleted();
            backgroundErrors.OnCompleted();
            cts.Dispose();
        }
    }
}
