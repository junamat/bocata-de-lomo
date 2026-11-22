using System.Text.Json;
using RefereeHubApi.Secrets;

namespace RefereeHubApi.Auth
{
    // kept abstract so the token source (e.g. a bot client later) can change without touching anything else.
    public interface IRefereeCredentials
    {
        Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    }

    // the ref's OAuth app and tokens. both only ever live in the OS keychain.
    public sealed class RefereeProfile : IRefereeCredentials
    {
        public static readonly TimeSpan REFRESH_MARGIN = TimeSpan.FromMinutes(5);

        private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly ISecretStore store;
        private readonly OsuOAuthClient oauth;
        private readonly TimeProvider time;
        private readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);
        private OAuthTokens? tokens;

        public string Name { get; }

        public OAuthClientConfig Client { get; }

        public bool HasTokens => tokens != null;

        private RefereeProfile(string name, OAuthClientConfig client, OAuthTokens? tokens, ISecretStore store, OsuOAuthClient oauth, TimeProvider time)
        {
            Name = name;
            Client = client;
            this.tokens = tokens;
            this.store = store;
            this.oauth = oauth;
            this.time = time;
        }

        private static string clientKey(string profile) => $"refereehubapi/{profile}/client";
        private static string tokensKey(string profile) => $"refereehubapi/{profile}/tokens";

        public static async Task<RefereeProfile> CreateAsync(string name, OAuthClientConfig client, ISecretStore store, OsuOAuthClient oauth, TimeProvider? time = null, CancellationToken cancellationToken = default)
        {
            await store.SetAsync(clientKey(name), JsonSerializer.Serialize(new StoredClient(client.ClientId, client.ClientSecret, client.CallbackPort, client.Scopes.ToArray()), json_options), cancellationToken).ConfigureAwait(false);
            await store.DeleteAsync(tokensKey(name), cancellationToken).ConfigureAwait(false);
            return new RefereeProfile(name, client, null, store, oauth, time ?? TimeProvider.System);
        }

        public static async Task<RefereeProfile?> LoadAsync(string name, ISecretStore store, OsuOAuthClient oauth, TimeProvider? time = null, CancellationToken cancellationToken = default)
        {
            string? clientJson = await store.GetAsync(clientKey(name), cancellationToken).ConfigureAwait(false);
            if (clientJson == null)
                return null;

            var stored = JsonSerializer.Deserialize<StoredClient>(clientJson, json_options)
                         ?? throw new SecretStoreException($"Stored client for profile '{name}' is unreadable.");

            var client = new OAuthClientConfig
            {
                ClientId = stored.ClientId,
                ClientSecret = stored.ClientSecret,
                CallbackPort = stored.CallbackPort,
                Scopes = stored.Scopes,
            };

            string? tokensJson = await store.GetAsync(tokensKey(name), cancellationToken).ConfigureAwait(false);
            var tokens = tokensJson == null ? null : JsonSerializer.Deserialize<OAuthTokens>(tokensJson, json_options);

            return new RefereeProfile(name, client, tokens, store, oauth, time ?? TimeProvider.System);
        }

        public async Task LoginAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken = default)
        {
            var newTokens = await oauth.LoginAsync(Client, openBrowser, cancellationToken).ConfigureAwait(false);
            await setTokens(newTokens, cancellationToken).ConfigureAwait(false);
        }

        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                tokens = null;
                await store.DeleteAsync(tokensKey(Name), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                tokenLock.Release();
            }
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (tokens == null)
                    throw new ReauthenticationRequiredException($"Profile '{Name}' is not logged in.");

                if (tokens.ExpiresAt - time.GetUtcNow() > REFRESH_MARGIN)
                    return tokens.AccessToken;

                OAuthTokens refreshed;

                try
                {
                    refreshed = await oauth.RefreshAsync(Client, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
                }
                catch (ReauthenticationRequiredException)
                {
                    tokens = null;
                    await store.DeleteAsync(tokensKey(Name), cancellationToken).ConfigureAwait(false);
                    throw;
                }

                tokens = refreshed;
                await store.SetAsync(tokensKey(Name), JsonSerializer.Serialize(refreshed, json_options), cancellationToken).ConfigureAwait(false);
                return refreshed.AccessToken;
            }
            finally
            {
                tokenLock.Release();
            }
        }

        private async Task setTokens(OAuthTokens newTokens, CancellationToken cancellationToken)
        {
            await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                tokens = newTokens;
                await store.SetAsync(tokensKey(Name), JsonSerializer.Serialize(newTokens, json_options), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                tokenLock.Release();
            }
        }

        private sealed record StoredClient(string ClientId, string ClientSecret, int CallbackPort, string[] Scopes);
    }
}
