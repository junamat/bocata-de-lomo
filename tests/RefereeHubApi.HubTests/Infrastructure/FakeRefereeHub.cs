using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RefereeHubApi.Hub;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Hub.Models.Responses;
using HubBase = Microsoft.AspNetCore.SignalR.Hub;

namespace RefereeHubApi.Tests.Infrastructure
{
    public sealed class FakeHubState
    {
        public ConcurrentQueue<(string Method, object?[] Args)> Calls { get; } = new ConcurrentQueue<(string, object?[])>();

        public List<long> ListedRooms { get; } = new List<long>();

        public ConcurrentDictionary<string, string> Errors { get; } = new ConcurrentDictionary<string, string>();

        public Func<long, RoomJoinedResponse> RoomFactory { get; set; } = id => RoomFixtures.DefaultRoom(id);

        public long NextRoomId { get; set; } = 1000;

        public Func<IHubCallerClients, long, Task>? BeforeRoomReturn { get; set; }

        public void Record(string method, params object?[] args)
        {
            Calls.Enqueue((method, args));

            if (Errors.TryGetValue(method, out string? message))
                throw new HubException(message);
        }

        public IEnumerable<string> MethodNames => Calls.Select(c => c.Method);
    }

    public sealed class FakeRefereeHub : HubBase
    {
        private readonly FakeHubState state;

        public FakeRefereeHub(FakeHubState state)
        {
            this.state = state;
        }

        public async Task<RoomJoinedResponse> MakeRoom(MakeRoomRequest request)
        {
            state.Record(nameof(MakeRoom), request);
            long id = state.NextRoomId++;
            lock (state.ListedRooms)
                state.ListedRooms.Add(id);

            if (state.BeforeRoomReturn != null)
                await state.BeforeRoomReturn(Clients, id);

            var room = state.RoomFactory(id);
            room.Name = request.RoomName;
            return room;
        }

        public async Task<RoomJoinedResponse> JoinRoom(long roomId)
        {
            state.Record(nameof(JoinRoom), roomId);

            if (state.BeforeRoomReturn != null)
                await state.BeforeRoomReturn(Clients, roomId);

            return state.RoomFactory(roomId);
        }

        public ListRoomsResponse ListRooms()
        {
            state.Record(nameof(ListRooms));
            lock (state.ListedRooms)
                return new ListRoomsResponse { RoomIds = state.ListedRooms.ToArray() };
        }

        public void CloseRoom(long roomId) => state.Record(nameof(CloseRoom), roomId);
        public void InvitePlayer(long roomId, int userId) => state.Record(nameof(InvitePlayer), roomId, userId);
        public void KickPlayer(long roomId, int userId) => state.Record(nameof(KickPlayer), roomId, userId);
        public void BanUser(long roomId, int bannedUserId) => state.Record(nameof(BanUser), roomId, bannedUserId);
        public void AddReferee(long roomId, int targetUserId) => state.Record(nameof(AddReferee), roomId, targetUserId);
        public void RemoveReferee(long roomId, int targetUserId) => state.Record(nameof(RemoveReferee), roomId, targetUserId);
        public void ChangeRoomSettings(long roomId, ChangeRoomSettingsRequest request) => state.Record(nameof(ChangeRoomSettings), roomId, request);
        public void EditCurrentPlaylistItem(long roomId, EditCurrentPlaylistItemRequest request) => state.Record(nameof(EditCurrentPlaylistItem), roomId, request);
        public void AddPlaylistItem(long roomId, AddPlaylistItemRequest request) => state.Record(nameof(AddPlaylistItem), roomId, request);
        public void EditPlaylistItem(long roomId, EditPlaylistItemRequest request) => state.Record(nameof(EditPlaylistItem), roomId, request);
        public void RemovePlaylistItem(long roomId, RemovePlaylistItemRequest request) => state.Record(nameof(RemovePlaylistItem), roomId, request);
        public void Roll(long roomId, RollRequest? request) => state.Record(nameof(Roll), roomId, request);
        public void MoveUser(long roomId, MoveUserRequest request) => state.Record(nameof(MoveUser), roomId, request);
        public void SetLockState(long roomId, SetLockStateRequest request) => state.Record(nameof(SetLockState), roomId, request);
        public void StartMatch(long roomId, StartGameplayRequest request) => state.Record(nameof(StartMatch), roomId, request);
        public void StopMatchCountdown(long roomId) => state.Record(nameof(StopMatchCountdown), roomId);
        public void AbortMatch(long roomId) => state.Record(nameof(AbortMatch), roomId);
    }

    public sealed class FakeHubServer : IAsyncDisposable
    {
        private readonly WebApplication app;

        public FakeHubState State { get; }

        public TestServer Server { get; }

        public IHubContext<FakeRefereeHub> Context => app.Services.GetRequiredService<IHubContext<FakeRefereeHub>>();

        private FakeHubServer(WebApplication app, FakeHubState state)
        {
            this.app = app;
            State = state;
            Server = app.GetTestServer();
        }

        public static async Task<FakeHubServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSignalR();

            var state = new FakeHubState();
            builder.Services.AddSingleton(state);

            var app = builder.Build();
            app.MapHub<FakeRefereeHub>("/referee");
            await app.StartAsync();

            return new FakeHubServer(app, state);
        }

        public RefereeHubConnectionOptions ConnectionOptions() => new RefereeHubConnectionOptions
        {
            HubUrl = new Uri("http://localhost/referee"),
            UseStatefulReconnect = false,
            ConfigureHttpConnection = o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            },
        };

        public Task SendAsync(string eventName, object payload) => Context.Clients.All.SendAsync(eventName, payload);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
