using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RefereeHubApi.Auth;

namespace RefereeHubApi.Api
{
    public class OsuApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public OsuApiException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

    // one instance per account: every request goes through its limiter (osu! allows 60/min).
    public sealed class OsuApiClient : IDisposable
    {
        public const int DEFAULT_REQUESTS_PER_MINUTE = 60;

        public static readonly Uri DEFAULT_BASE_URL = new Uri("https://osu.ppy.sh/api/v2/");

        private readonly HttpClient http;
        private readonly IRefereeCredentials credentials;
        private readonly Uri baseUrl;
        private readonly RateLimiter limiter;
        private readonly ILogger logger;

        public OsuApiClient(HttpClient http, IRefereeCredentials credentials, Uri? baseUrl = null, int requestsPerMinute = DEFAULT_REQUESTS_PER_MINUTE, ILogger? logger = null)
        {
            this.http = http;
            this.credentials = credentials;
            this.baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            this.logger = logger ?? NullLogger.Instance;

            limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = requestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 12,
                QueueLimit = 1000,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
        }

        public Task<ApiUser> GetMeAsync(CancellationToken ct = default)
            => send<ApiUser>(HttpMethod.Get, "me", null, ct);

        public async Task<ChatMessage[]> GetChannelMessagesAsync(long channelId, long? since = null, int limit = 50, CancellationToken ct = default)
        {
            string path = $"chat/channels/{channelId}/messages?limit={Math.Clamp(limit, 1, 50)}";
            if (since != null)
                path += $"&since={since.Value}";

            var messages = await send<ChatMessage[]>(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            return messages.OrderBy(m => m.MessageId).ToArray();
        }

        public Task<ChatMessage> SendChannelMessageAsync(long channelId, string message, bool isAction = false, CancellationToken ct = default)
            => send<ChatMessage>(HttpMethod.Post, $"chat/channels/{channelId}/messages", () => JsonContent.Create(new { message, is_action = isAction }), ct);

        public Task JoinChannelAsync(long channelId, int userId, CancellationToken ct = default)
            => send<object>(HttpMethod.Put, $"chat/channels/{channelId}/users/{userId}", null, ct, readBody: false);

        public Task ChatAckAsync(CancellationToken ct = default)
            => send<object>(HttpMethod.Post, "chat/ack", null, ct, readBody: false);

        public async Task<IReadOnlyList<MultiplayerScore>> GetPlaylistItemScoresAsync(long roomId, long playlistItemId, CancellationToken ct = default)
        {
            var scores = new List<MultiplayerScore>();
            string? cursor = null;

            do
            {
                string path = $"rooms/{roomId}/playlist/{playlistItemId}/scores?limit=50";
                if (cursor != null)
                    path += $"&cursor_string={Uri.EscapeDataString(cursor)}";

                var page = await send<MultiplayerScoresPage>(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
                scores.AddRange(page.Scores);
                cursor = page.Scores.Length > 0 ? page.CursorString : null;
            } while (cursor != null);

            return scores;
        }

        private async Task<T> send<T>(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct, bool readBody = true)
        {
            for (int attempt = 0;; attempt++)
            {
                using (var lease = await limiter.AcquireAsync(1, ct).ConfigureAwait(false))
                {
                    if (!lease.IsAcquired)
                        throw new OsuApiException(HttpStatusCode.TooManyRequests, "Local API rate limit queue is full.");
                }

                string token = await credentials.GetAccessTokenAsync(ct).ConfigureAwait(false);

                using var request = new HttpRequestMessage(method, new Uri(baseUrl, path)) { Content = content?.Invoke() };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    logger.LogWarning("osu! API returned 429 for {Path}, retrying in {Delay}", path, delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new OsuApiException(response.StatusCode, $"{method} {path} failed ({(int)response.StatusCode}): {truncate(body)}");
                }

                if (!readBody)
                    return default!;

                return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false)
                       ?? throw new OsuApiException(response.StatusCode, $"{method} {path} returned an empty body.");
            }
        }

        private static string truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;

        public void Dispose() => limiter.Dispose();
    }
}
