using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefereeHubApi.Auth
{
    public sealed record OAuthClientConfig
    {
        public const int DEFAULT_CALLBACK_PORT = 7270;

        public static readonly string[] DEFAULT_SCOPES =
        [
            "identify",
            "public",
            "chat.read",
            "chat.write",
            "chat.write_manage",
            "multiplayer.write_manage",
        ];

        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }

        public int CallbackPort { get; init; } = DEFAULT_CALLBACK_PORT;

        public IReadOnlyList<string> Scopes { get; init; } = DEFAULT_SCOPES;

        public Uri RedirectUri => new Uri($"http://localhost:{CallbackPort}/callback");
    }

    public sealed record OAuthTokens
    {
        [JsonPropertyName("access_token")]
        public required string AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public required string RefreshToken { get; init; }

        [JsonPropertyName("expires_at")]
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    public class OAuthException : Exception
    {
        public string? Error { get; }

        public OAuthException(string message, string? error = null, Exception? inner = null)
            : base(message, inner)
        {
            Error = error;
        }
    }

    public class ReauthenticationRequiredException : OAuthException
    {
        public ReauthenticationRequiredException(string message, string? error = null, Exception? inner = null)
            : base(message, error, inner)
        {
        }
    }

    public sealed class OsuOAuthClient
    {
        public static readonly Uri DEFAULT_BASE_URL = new Uri("https://osu.ppy.sh/");

        private readonly HttpClient http;
        private readonly Uri baseUrl;
        private readonly TimeProvider time;

        public OsuOAuthClient(HttpClient http, Uri? baseUrl = null, TimeProvider? time = null)
        {
            this.http = http;
            this.baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            this.time = time ?? TimeProvider.System;
        }

        public static string CreateState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public Uri BuildAuthorizeUri(OAuthClientConfig client, string state)
        {
            string query = string.Join('&',
                $"client_id={Uri.EscapeDataString(client.ClientId)}",
                $"redirect_uri={Uri.EscapeDataString(client.RedirectUri.ToString())}",
                "response_type=code",
                $"scope={Uri.EscapeDataString(string.Join(' ', client.Scopes))}",
                $"state={Uri.EscapeDataString(state)}");

            return new Uri(baseUrl, $"oauth/authorize?{query}");
        }

        public async Task<OAuthTokens> LoginAsync(OAuthClientConfig client, Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default)
        {
            string state = CreateState();

            using var listener = new LoopbackCallbackListener(client.CallbackPort);
            listener.Start();

            var waitForCode = listener.WaitForCodeAsync(state, cancellationToken);
            await openBrowser(BuildAuthorizeUri(client, state)).ConfigureAwait(false);

            string code = await waitForCode.ConfigureAwait(false);
            return await ExchangeCodeAsync(client, code, cancellationToken).ConfigureAwait(false);
        }

        public Task<OAuthTokens> ExchangeCodeAsync(OAuthClientConfig client, string code, CancellationToken cancellationToken = default)
            => requestToken(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = client.RedirectUri.ToString(),
            }, isRefresh: false, cancellationToken);

        public Task<OAuthTokens> RefreshAsync(OAuthClientConfig client, string refreshToken, CancellationToken cancellationToken = default)
            => requestToken(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = string.Join(' ', client.Scopes),
            }, isRefresh: true, cancellationToken);

        private async Task<OAuthTokens> requestToken(Dictionary<string, string> form, bool isRefresh, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "oauth/token"))
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                TokenError? error = null;

                try
                {
                    error = await response.Content.ReadFromJsonAsync<TokenError>(cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                }

                string message = $"osu! token request failed ({(int)response.StatusCode}): {error?.Error} {error?.ErrorDescription ?? error?.Message}".TrimEnd();

                if (isRefresh && (int)response.StatusCode is 400 or 401)
                    throw new ReauthenticationRequiredException(message, error?.Error);

                throw new OAuthException(message, error?.Error);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
                        ?? throw new OAuthException("osu! token response was empty.");

            if (string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.RefreshToken))
                throw new OAuthException("osu! token response lacked access_token or refresh_token.");

            return new OAuthTokens
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAt = time.GetUtcNow() + TimeSpan.FromSeconds(token.ExpiresIn),
            };
        }

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = string.Empty;

            [JsonPropertyName("expires_in")]
            public long ExpiresIn { get; set; }
        }

        private sealed class TokenError
        {
            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("error_description")]
            public string? ErrorDescription { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }
    }
}
