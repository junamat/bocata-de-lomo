using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RefereeHubApi.Auth;
using RefereeHubApi.Chat;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Responses;

namespace RefereeHubApi.Tests.Infrastructure
{
    public static class TestIds
    {
        public const int SELF = 2;
        public const int PLAYER_A = 100;
        public const int PLAYER_B = 101;
        public const int OTHER_REF = 50;
    }

    public static class RoomFixtures
    {
        public static RoomJoinedResponse DefaultRoom(long id) => new RoomJoinedResponse
        {
            RoomId = id,
            ChatChannelId = 555,
            Name = "TEST: (Red) vs (Blue)",
            Password = "pw",
            QueueMode = QueueMode.HostOnly,
            State = new MatchState { Type = MatchType.HeadToHead, Locked = false, Slots = [null, null] },
            Playlist = [new PlaylistItem { PlaylistItemId = 1, BeatmapId = 75, RulesetId = 0, Order = 0 }],
            Players = [],
            Referees = [new Referee { UserId = TestIds.SELF }],
        };
    }

    public sealed class FakeCredentials : IRefereeCredentials
    {
        public string Token { get; set; } = "test-token";

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);
    }

    public sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly List<(string Method, string PathPrefix, Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler)> routes = new();

        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new ConcurrentQueue<HttpRequestMessage>();

        public ConcurrentQueue<string?> Bodies { get; } = new ConcurrentQueue<string?>();

        public StubHttpHandler On(string method, string pathPrefix, Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            routes.Add((method, pathPrefix, r => Task.FromResult(handler(r))));
            return this;
        }

        public StubHttpHandler OnJson(string method, string pathPrefix, Func<HttpRequestMessage, object> body)
            => On(method, pathPrefix, r => Json(body(r)));

        public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        public int Count(string method, string pathPrefix)
            => Requests.Count(r => r.Method.Method == method && r.RequestUri!.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Bodies.Enqueue(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            string path = request.RequestUri!.AbsolutePath;

            // most specific (longest) prefix wins.
            foreach (var route in routes.OrderByDescending(r => r.PathPrefix.Length))
            {
                if (route.Method == request.Method.Method && path.StartsWith(route.PathPrefix, StringComparison.Ordinal))
                    return await route.Handler(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no stub for {request.Method} {path}") };
        }
    }

    public sealed class FakeNotificationSocket : INotificationSocket
    {
        private readonly Channel<string?> incoming = Channel.CreateUnbounded<string?>();

        public ConcurrentQueue<string> Sent { get; } = new ConcurrentQueue<string>();

        public string? AccessToken { get; private set; }

        public bool FailConnect { get; set; }

        public TaskCompletionSource Connected { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
        {
            if (FailConnect)
                throw new IOException("connect refused");

            AccessToken = accessToken;
            Connected.TrySetResult();
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            Sent.Enqueue(text);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
            => await incoming.Reader.ReadAsync(cancellationToken);

        public void Push(object message) => incoming.Writer.TryWrite(JsonSerializer.Serialize(message));

        public void CloseFromServer() => incoming.Writer.TryWrite(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public static class Wait
    {
        public static readonly TimeSpan DEFAULT = TimeSpan.FromSeconds(10);

        public static Task<T> For<T>(IObservable<T> source, Func<T, bool>? predicate = null, TimeSpan? timeout = null)
            => source.Where(predicate ?? (_ => true)).FirstAsync().Timeout(timeout ?? DEFAULT).ToTask();

        public static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? DEFAULT);

            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Condition not met in time.");

                await Task.Delay(10);
            }
        }
    }

    public static class ApiFixtures
    {
        public static object Me => new { id = TestIds.SELF, username = "TestRef" };

        public static object Message(long id, long channelId, string content, int sender = TestIds.PLAYER_A, DateTimeOffset? at = null) => new
        {
            message_id = id,
            sender_id = sender,
            channel_id = channelId,
            timestamp = (at ?? DateTimeOffset.UtcNow).ToString("O"),
            content,
            is_action = false,
            type = "plain",
        };
    }
}
