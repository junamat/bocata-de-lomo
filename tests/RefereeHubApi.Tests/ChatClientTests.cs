using System.Net;
using RefereeHubApi.Api;
using RefereeHubApi.Chat;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class ChatClientTests
    {
        private const long channel_id = 555;

        private readonly StubHttpHandler http = new StubHttpHandler();
        private readonly List<object> serverMessages = new List<object>();

        public ChatClientTests()
        {
            http.On("PUT", $"/api/v2/chat/channels/{channel_id}/users/", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            http.On("POST", "/api/v2/chat/ack", _ => StubHttpHandler.Json(new { silences = Array.Empty<object>() }));
            http.OnJson("GET", $"/api/v2/chat/channels/{channel_id}/messages", r =>
            {
                string query = r.RequestUri!.Query;
                long since = query.Contains("since=") ? long.Parse(query.Split("since=")[1].Split('&')[0]) : 0;

                lock (serverMessages)
                    return serverMessages.Where(m => (long)m.GetType().GetProperty("message_id")!.GetValue(m)! > since).ToArray();
            });
            http.OnJson("POST", $"/api/v2/chat/channels/{channel_id}/messages", _ =>
            {
                var m = ApiFixtures.Message(999, channel_id, "hello", sender: TestIds.SELF);
                lock (serverMessages)
                    serverMessages.Add(m);
                return m;
            });
        }

        private void serverHas(object message)
        {
            lock (serverMessages)
                serverMessages.Add(message);
        }

        private (ChatClient chat, OsuApiClient api) create(ChatClientOptions options)
        {
            var api = new OsuApiClient(new HttpClient(http), new FakeCredentials(), new Uri("http://api.test/api/v2/"), requestsPerMinute: 10_000);
            return (new ChatClient(api, new FakeCredentials(), TestIds.SELF, options), api);
        }

        private static ChatClientOptions fastOptions(Func<INotificationSocket>? socket = null) => new ChatClientOptions
        {
            UseWebSocket = socket != null,
            SocketFactory = socket ?? (() => throw new InvalidOperationException()),
            PollInterval = TimeSpan.FromMilliseconds(20),
            ReconcileInterval = TimeSpan.FromMilliseconds(50),
            AckInterval = TimeSpan.FromMilliseconds(50),
            WebSocketRetryInterval = TimeSpan.FromMilliseconds(50),
            MissGrace = TimeSpan.FromSeconds(5),
        };

        [Fact]
        public async Task PollingDeliversHistoryThenNewMessagesOnce()
        {
            serverHas(ApiFixtures.Message(1, channel_id, "history"));

            var (chat, api) = create(fastOptions());
            await using var _ = chat;
            using var __ = api;

            var channel = await chat.OpenChannelAsync(channel_id);
            var received = new List<ChatMessage>();
            using var sub = channel.Messages.Subscribe(m => { lock (received) received.Add(m); });

            serverHas(ApiFixtures.Message(2, channel_id, "live"));
            await Wait.Until(() => { lock (received) return received.Count == 2; });

            var sent = await channel.SendAsync("hello");
            await Task.Delay(100);

            lock (received)
                Assert.Equal(["history", "live", "hello"], received.Select(m => m.Content));
            Assert.Equal(999, sent.MessageId);
            Assert.Equal(ChatTransport.Polling, chat.CurrentTransport);
            Assert.True(http.Count("PUT", $"/api/v2/chat/channels/{channel_id}/users/{TestIds.SELF}") == 1);
        }

        [Fact]
        public async Task WebSocketDeliversAndStartsChat()
        {
            var socket = new FakeNotificationSocket();
            var (chat, api) = create(fastOptions(() => socket));
            await using var _ = chat;
            using var __ = api;

            var channel = await chat.OpenChannelAsync(channel_id);
            await socket.Connected.Task.WaitAsync(Wait.DEFAULT);
            await Wait.For(chat.Transport, t => t == ChatTransport.WebSocket);

            Assert.Equal("{\"event\":\"chat.start\"}", Assert.Single(socket.Sent));
            Assert.Equal("test-token", socket.AccessToken);

            var next = Wait.For(channel.Messages);
            socket.Push(new
            {
                @event = "chat.message.new",
                data = new
                {
                    messages = new[] { ApiFixtures.Message(10, channel_id, "via socket") },
                    users = new[] { new { id = TestIds.PLAYER_A, username = "PlayerA" } },
                },
            });

            var m = await next;
            Assert.Equal("via socket", m.Content);
            Assert.Equal("PlayerA", m.Sender!.Username);

            // reconciliation sees nothing new: stays on websocket.
            await Task.Delay(200);
            Assert.Equal(ChatTransport.WebSocket, chat.CurrentTransport);
            Assert.False(chat.WebSocketDropsMessages);
        }

        [Fact]
        public async Task ReconciliationDetectsDroppedMessagesAndFallsBack()
        {
            var socket = new FakeNotificationSocket();
            var (chat, api) = create(fastOptions(() => socket));
            await using var _ = chat;
            using var __ = api;

            var channel = await chat.OpenChannelAsync(channel_id);
            await Wait.For(chat.Transport, t => t == ChatTransport.WebSocket);

            // the server has an old message the socket never delivered (unverified token behaviour).
            serverHas(ApiFixtures.Message(20, channel_id, "missed", at: DateTimeOffset.UtcNow.AddSeconds(-30)));

            await Wait.For(chat.Transport, t => t == ChatTransport.Polling);
            Assert.True(chat.WebSocketDropsMessages);
            Assert.Equal(20, channel.LastMessageId);
        }

        [Fact]
        public async Task SocketFailureFallsBackToPolling()
        {
            var socket = new FakeNotificationSocket { FailConnect = true };
            var (chat, api) = create(fastOptions(() => socket));
            await using var _ = chat;
            using var __ = api;

            var channel = await chat.OpenChannelAsync(channel_id);
            await Wait.For(chat.Transport, t => t == ChatTransport.Polling);

            var next = Wait.For(channel.Messages, m => m.MessageId == 30);
            serverHas(ApiFixtures.Message(30, channel_id, "polled"));
            Assert.Equal("polled", (await next).Content);
            Assert.False(chat.WebSocketDropsMessages);
        }

        private const long pm_channel_id = 777;

        private void stubPrivateChannel()
        {
            http.OnJson("POST", "/api/v2/chat/new", _ =>
            {
                var m = ApiFixtures.Message(1000, pm_channel_id, "hi", sender: TestIds.SELF);
                lock (serverMessages)
                    serverMessages.Add(m);
                return new { channel = new { channel_id = pm_channel_id, type = "PM", name = "PlayerA" }, message = m };
            });
            http.OnJson("POST", $"/api/v2/chat/channels/{pm_channel_id}/messages", _ => ApiFixtures.Message(1001, pm_channel_id, "again", sender: TestIds.SELF));
            http.OnJson("GET", $"/api/v2/chat/channels/{pm_channel_id}/messages", r =>
            {
                lock (serverMessages)
                    return serverMessages.Where(m => (long)m.GetType().GetProperty("channel_id")!.GetValue(m)! == pm_channel_id).ToArray();
            });
        }

        [Fact]
        public async Task PrivateMessageOpensChannelThroughChatNewOnce()
        {
            stubPrivateChannel();
            var (chat, api) = create(fastOptions());
            await using var _ = chat;
            using var __ = api;

            var opened = Wait.For(chat.PrivateChannelOpened);
            var first = await chat.SendPrivateMessageAsync(TestIds.PLAYER_A, "hi");
            var channel = await opened;
            var second = await chat.SendPrivateMessageAsync(TestIds.PLAYER_A, "again");

            Assert.Equal(1000, first.MessageId);
            Assert.Equal(1001, second.MessageId);
            Assert.Equal(ChatChannelKind.Private, channel.Kind);
            Assert.Equal(TestIds.PLAYER_A, channel.PeerUserId);
            Assert.Same(channel, chat.GetPrivateChannel(TestIds.PLAYER_A));
            Assert.Equal(1, http.Count("POST", "/api/v2/chat/new"));
            Assert.Contains(http.Bodies, b => b != null && b.Contains($"\"target_id\":{TestIds.PLAYER_A}"));
            await Assert.ThrowsAsync<ArgumentException>(() => chat.SendPrivateMessageAsync(TestIds.SELF, "me"));
        }

        [Fact]
        public async Task KnownPrivateChannelIsPolled()
        {
            stubPrivateChannel();
            var (chat, api) = create(fastOptions());
            await using var _ = chat;
            using var __ = api;

            await chat.SendPrivateMessageAsync(TestIds.PLAYER_A, "hi");
            var channel = chat.GetPrivateChannel(TestIds.PLAYER_A)!;

            var reply = Wait.For(channel.NewMessages, m => m.MessageId == 1002);
            serverHas(ApiFixtures.Message(1002, pm_channel_id, "reply", sender: TestIds.PLAYER_A));
            Assert.Equal("reply", (await reply).Content);
        }

        [Fact]
        public async Task IncomingPrivateMessageOnSocketOpensChannel()
        {
            var socket = new FakeNotificationSocket();
            var (chat, api) = create(fastOptions(() => socket));
            await using var _ = chat;
            using var __ = api;

            await chat.OpenChannelAsync(channel_id);
            await Wait.For(chat.Transport, t => t == ChatTransport.WebSocket);

            ChatMessage? firstSeen = null;
            using var sub = chat.PrivateChannelOpened.Subscribe(c => c.NewMessages.Subscribe(m => firstSeen ??= m));

            socket.Push(new
            {
                @event = "chat.message.new",
                data = new
                {
                    messages = new[] { ApiFixtures.Message(50, 888, "hey ref", sender: TestIds.PLAYER_B) },
                    users = new[] { new { id = TestIds.PLAYER_B, username = "PlayerB" } },
                },
            });

            await Wait.Until(() => firstSeen != null);
            var channel = chat.GetPrivateChannel(TestIds.PLAYER_B)!;
            Assert.Equal(888, channel.ChannelId);
            Assert.Equal("hey ref", firstSeen!.Content);
            Assert.Equal("PlayerB", firstSeen.Sender!.Username);

            // our own message to an unknown channel (sent from another client) doesn't open anything.
            socket.Push(new
            {
                @event = "chat.message.new",
                data = new { messages = new[] { ApiFixtures.Message(51, 889, "from lazer", sender: TestIds.SELF) }, users = Array.Empty<object>() },
            });
            await Task.Delay(100);
            Assert.Single(chat.PrivateChannels);
        }
    }
}
