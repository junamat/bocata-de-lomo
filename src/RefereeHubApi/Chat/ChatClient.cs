using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RefereeHubApi.Api;
using RefereeHubApi.Auth;

namespace RefereeHubApi.Chat
{
    public enum ChatTransport
    {
        Starting,

        WebSocket,

        Polling,
    }

    public enum ChatChannelKind
    {
        Room,

        Private,
    }

    public sealed class ChatClientOptions
    {
        public static readonly Uri PRODUCTION_NOTIFY_URL = new Uri("wss://notify.ppy.sh");

        public Uri NotifyUrl { get; set; } = PRODUCTION_NOTIFY_URL;

        public bool UseWebSocket { get; set; } = true;

        // intervals are per request, round-robin over open channels, so the total rate doesn't grow with room count.
        public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(15);

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

        public TimeSpan WebSocketRetryInterval { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan AckInterval { get; set; } = TimeSpan.FromSeconds(60);

        // a message only counts as missed by the socket once it's this old, so one still in flight doesn't trigger a fallback.
        public TimeSpan MissGrace { get; set; } = TimeSpan.FromSeconds(5);

        public int ReplayBufferSize { get; set; } = 500;

        public Func<INotificationSocket> SocketFactory { get; set; } = () => new ClientNotificationSocket();
    }

    // the notification websocket silently drops chat for tokens that aren't "verified", with no error.
    // so even while it looks healthy we poll now and then; if polling finds old messages the socket never
    // delivered, we stop trusting it and switch to polling for good.
    public sealed class ChatClient : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly OsuApiClient api;
        private readonly IRefereeCredentials credentials;
        private readonly int selfUserId;
        private readonly ChatClientOptions options;
        private readonly TimeProvider time;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<long, ChatChannel> channels = new ConcurrentDictionary<long, ChatChannel>();
        private readonly ConcurrentDictionary<int, ChatChannel> privateChannels = new ConcurrentDictionary<int, ChatChannel>();
        private readonly Subject<ChatChannel> privateChannelOpened = new Subject<ChatChannel>();
        private readonly SemaphoreSlim privateOpenLock = new SemaphoreSlim(1, 1);
        private readonly BehaviorSubject<ChatTransport> transport = new BehaviorSubject<ChatTransport>(ChatTransport.Starting);
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly object startLock = new object();
        private readonly List<Task> loops = new List<Task>();
        private volatile bool websocketUnreliable;
        private int pollCursor;
        private int reconcileCursor;

        public ChatClient(OsuApiClient api, IRefereeCredentials credentials, int selfUserId, ChatClientOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
        {
            this.api = api;
            this.credentials = credentials;
            this.selfUserId = selfUserId;
            this.options = options ?? new ChatClientOptions();
            this.time = time ?? TimeProvider.System;
            this.logger = logger ?? NullLogger.Instance;
        }

        public IObservable<ChatTransport> Transport => transport.AsObservable();

        public ChatTransport CurrentTransport => transport.Value;

        public bool WebSocketDropsMessages => websocketUnreliable;

        public IReadOnlyCollection<ChatChannel> Channels => channels.Values.ToArray();

        public IReadOnlyCollection<ChatChannel> PrivateChannels => privateChannels.Values.ToArray();

        // a PM channel became known: we sent the first message, or someone messaged us (websocket only; polling can't discover new PMs).
        // fires before the channel's first message is delivered, so NewMessages subscribers see it.
        public IObservable<ChatChannel> PrivateChannelOpened => privateChannelOpened.AsObservable();

        public ChatChannel? GetPrivateChannel(int userId) => privateChannels.GetValueOrDefault(userId);

        public async Task<ChatChannel> OpenChannelAsync(long channelId, CancellationToken cancellationToken = default)
        {
            if (channels.TryGetValue(channelId, out var existing))
                return existing;

            try
            {
                await api.JoinChannelAsync(channelId, selfUserId, cancellationToken).ConfigureAwait(false);
            }
            catch (OsuApiException ex)
            {
                // the hub's JoinRoom probably joins the channel already (unverified), so reading may still work.
                logger.LogWarning("Could not join chat channel {Channel}: {Message}", channelId, ex.Message);
            }

            var channel = new ChatChannel(channelId, ChatChannelKind.Room, null, this, options.ReplayBufferSize);
            channel = channels.GetOrAdd(channelId, channel);

            var history = await api.GetChannelMessagesAsync(channelId, null, 50, cancellationToken).ConfigureAwait(false);
            channel.Deliver(history);

            ensureStarted();
            return channel;
        }

        public void CloseChannel(long channelId)
        {
            if (!channels.TryRemove(channelId, out var channel))
                return;

            if (channel.PeerUserId is int peer)
                privateChannels.TryRemove(new KeyValuePair<int, ChatChannel>(peer, channel));

            channel.Complete();
        }

        // one recipient per call, only on an explicit request from the ref; nothing in the library sends PMs on its own.
        public async Task<ChatMessage> SendPrivateMessageAsync(int userId, string message, bool isAction = false, CancellationToken cancellationToken = default)
        {
            if (userId == selfUserId)
                throw new ArgumentException("Can't send a private message to yourself.", nameof(userId));

            if (privateChannels.TryGetValue(userId, out var known))
                return await known.SendAsync(message, isAction, cancellationToken).ConfigureAwait(false);

            await privateOpenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (privateChannels.TryGetValue(userId, out known))
                    return await known.SendAsync(message, isAction, cancellationToken).ConfigureAwait(false);

                var response = await api.CreatePrivateChatAsync(userId, message, isAction, Guid.NewGuid().ToString(), cancellationToken).ConfigureAwait(false);
                var channel = registerPrivate(response.Channel.ChannelId, userId);
                channel.Deliver([response.Message]);
                return response.Message;
            }
            finally
            {
                privateOpenLock.Release();
            }
        }

        private ChatChannel registerPrivate(long channelId, int peerUserId)
        {
            bool created = false;

            var channel = channels.GetOrAdd(channelId, id =>
            {
                created = true;
                return new ChatChannel(id, ChatChannelKind.Private, peerUserId, this, options.ReplayBufferSize);
            });

            privateChannels[peerUserId] = channel;

            if (created)
            {
                privateChannelOpened.OnNext(channel);
                ensureStarted();
            }

            return channel;
        }

        internal async Task<ChatMessage> SendAsync(ChatChannel channel, string message, bool isAction, CancellationToken cancellationToken)
        {
            var sent = await api.SendChannelMessageAsync(channel.ChannelId, message, isAction, cancellationToken).ConfigureAwait(false);
            channel.Deliver([sent]);
            return sent;
        }

        private void ensureStarted()
        {
            lock (startLock)
            {
                if (loops.Count > 0)
                    return;

                if (!options.UseWebSocket)
                    transport.OnNext(ChatTransport.Polling);
                else
                    loops.Add(Task.Run(() => webSocketLoop(cts.Token)));

                loops.Add(Task.Run(() => pollLoop(cts.Token)));
                loops.Add(Task.Run(() => reconcileLoop(cts.Token)));
                loops.Add(Task.Run(() => ackLoop(cts.Token)));
            }
        }

        #region WebSocket

        private async Task webSocketLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !websocketUnreliable)
            {
                try
                {
                    await using var socket = options.SocketFactory();
                    string token = await credentials.GetAccessTokenAsync(ct).ConfigureAwait(false);

                    await socket.ConnectAsync(options.NotifyUrl, token, ct).ConfigureAwait(false);
                    await socket.SendTextAsync("{\"event\":\"chat.start\"}", ct).ConfigureAwait(false);

                    if (!websocketUnreliable)
                        transport.OnNext(ChatTransport.WebSocket);

                    logger.LogInformation("Chat websocket connected");

                    while (!ct.IsCancellationRequested && !websocketUnreliable)
                    {
                        string? text = await socket.ReceiveTextAsync(ct).ConfigureAwait(false);

                        if (text == null)
                            throw new IOException("Chat websocket closed by server.");

                        if (!handleSocketMessage(text))
                            throw new IOException("Chat websocket session logged out.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Chat websocket failed, polling until retry: {Message}", ex.Message);
                    transport.OnNext(ChatTransport.Polling);

                    try
                    {
                        await Task.Delay(options.WebSocketRetryInterval, time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private bool handleSocketMessage(string text)
        {
            SocketMessage? message;

            try
            {
                message = JsonSerializer.Deserialize<SocketMessage>(text, json_options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Unreadable chat websocket message: {Message}", ex.Message);
                return true;
            }

            if (message == null)
                return true;

            if (message.Error != null)
            {
                logger.LogWarning("Chat websocket error: {Error}", message.Error);
                return true;
            }

            switch (message.Event)
            {
                case "logout":
                    return false;

                case "chat.message.new" when message.Data is JsonElement data:
                {
                    var payload = data.Deserialize<NewChatMessageData>(json_options);
                    if (payload == null)
                        return true;

                    var users = payload.Users.ToDictionary(u => u.Id);

                    foreach (var group in payload.Messages.GroupBy(m => m.ChannelId))
                    {
                        if (!channels.TryGetValue(group.Key, out var channel))
                        {
                            // room channels are opened explicitly and public ones are out of scope, so an unknown
                            // channel here is a PM. ours from another client (sender = self) can't tell us the peer.
                            int? peer = group.Select(m => m.SenderId).Cast<int?>().FirstOrDefault(id => id != selfUserId);
                            if (peer == null)
                                continue;

                            channel = registerPrivate(group.Key, peer.Value);
                        }

                        foreach (var m in group)
                            m.Sender ??= users.GetValueOrDefault(m.SenderId);

                        channel.Deliver(group);
                    }

                    return true;
                }

                case "chat.channel.join" when message.Data is JsonElement data:
                {
                    var joined = data.Deserialize<ApiChatChannel>(json_options);

                    if (joined is { Type: "PM", Users: { } users } && users.FirstOrDefault(u => u != selfUserId) is int peer and > 0)
                        registerPrivate(joined.ChannelId, peer);

                    return true;
                }

                default:
                    return true;
            }
        }

        #endregion

        #region Polling

        private async Task pollLoop(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(options.PollInterval, time);

            while (await waitTick(timer, ct).ConfigureAwait(false))
            {
                if (transport.Value != ChatTransport.Polling)
                    continue;

                var channel = nextChannel(ref pollCursor);
                if (channel == null)
                    continue;

                await fetchNew(channel, detectMisses: false, ct).ConfigureAwait(false);
            }
        }

        private async Task reconcileLoop(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(options.ReconcileInterval, time);

            while (await waitTick(timer, ct).ConfigureAwait(false))
            {
                if (transport.Value != ChatTransport.WebSocket)
                    continue;

                var channel = nextChannel(ref reconcileCursor);
                if (channel == null)
                    continue;

                await fetchNew(channel, detectMisses: true, ct).ConfigureAwait(false);
            }
        }

        private async Task ackLoop(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(options.AckInterval, time);

            while (await waitTick(timer, ct).ConfigureAwait(false))
            {
                if (channels.IsEmpty)
                    continue;

                try
                {
                    await api.ChatAckAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Chat ack failed: {Message}", ex.Message);
                }
            }
        }

        private async Task fetchNew(ChatChannel channel, bool detectMisses, CancellationToken ct)
        {
            ChatMessage[] messages;

            try
            {
                messages = await api.GetChannelMessagesAsync(channel.ChannelId, channel.LastMessageId, 50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Chat poll of channel {Channel} failed: {Message}", channel.ChannelId, ex.Message);
                return;
            }

            var fresh = channel.Deliver(messages);

            if (!detectMisses || fresh.Count == 0)
                return;

            var cutoff = time.GetUtcNow() - options.MissGrace;

            if (fresh.Any(m => m.Timestamp < cutoff))
            {
                logger.LogWarning("Chat websocket missed {Count} message(s) in channel {Channel}; switching to polling", fresh.Count, channel.ChannelId);
                websocketUnreliable = true;
                transport.OnNext(ChatTransport.Polling);
            }
        }

        private ChatChannel? nextChannel(ref int cursor)
        {
            var open = channels.Values.OrderBy(c => c.ChannelId).ToArray();
            if (open.Length == 0)
                return null;

            int index = (int)((uint)Interlocked.Increment(ref cursor) % (uint)open.Length);
            return open[index];
        }

        private static async Task<bool> waitTick(PeriodicTimer timer, CancellationToken ct)
        {
            try
            {
                return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            foreach (var channel in channels.Values)
                channel.Complete();

            channels.Clear();
            privateChannels.Clear();
            privateChannelOpened.OnCompleted();
            transport.OnCompleted();
            transport.Dispose();
            cts.Dispose();
        }

        private sealed class SocketMessage
        {
            [JsonPropertyName("event")]
            public string Event { get; set; } = string.Empty;

            [JsonPropertyName("data")]
            public JsonElement? Data { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }
        }

        private sealed class NewChatMessageData
        {
            [JsonPropertyName("messages")]
            public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

            [JsonPropertyName("users")]
            public List<ApiUser> Users { get; set; } = new List<ApiUser>();
        }
    }

    public sealed class ChatChannel
    {
        private readonly ChatClient client;
        private readonly ReplaySubject<ChatMessage> messages;
        private readonly Subject<ChatMessage> newMessages = new Subject<ChatMessage>();
        private readonly HashSet<long> seen = new HashSet<long>();
        private readonly object sync = new object();

        public long ChannelId { get; }

        public ChatChannelKind Kind { get; }

        // the other user, for private channels.
        public int? PeerUserId { get; }

        public long? LastMessageId { get; private set; }

        // replays recent history (up to the replay buffer size) to each new subscriber.
        public IObservable<ChatMessage> Messages => messages.AsObservable();

        // only messages delivered after subscribing; no history.
        public IObservable<ChatMessage> NewMessages => newMessages.AsObservable();

        internal ChatChannel(long channelId, ChatChannelKind kind, int? peerUserId, ChatClient client, int replayBufferSize)
        {
            ChannelId = channelId;
            Kind = kind;
            PeerUserId = peerUserId;
            this.client = client;
            messages = new ReplaySubject<ChatMessage>(replayBufferSize);
        }

        public Task<ChatMessage> SendAsync(string message, bool isAction = false, CancellationToken cancellationToken = default)
            => client.SendAsync(this, message, isAction, cancellationToken);

        internal IReadOnlyList<ChatMessage> Deliver(IEnumerable<ChatMessage> batch)
        {
            var fresh = new List<ChatMessage>();

            lock (sync)
            {
                foreach (var m in batch.OrderBy(m => m.MessageId))
                {
                    if (!seen.Add(m.MessageId))
                        continue;

                    if (LastMessageId == null || m.MessageId > LastMessageId)
                        LastMessageId = m.MessageId;

                    fresh.Add(m);
                    messages.OnNext(m);
                    newMessages.OnNext(m);
                }
            }

            return fresh;
        }

        internal void Complete()
        {
            lock (sync)
            {
                messages.OnCompleted();
                newMessages.OnCompleted();
            }
        }
    }
}
