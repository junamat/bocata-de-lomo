using System.Net;
using System.Net.Sockets;
using System.Web;
using Microsoft.Extensions.Time.Testing;
using RefereeHubApi.Auth;
using RefereeHubApi.Secrets;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class AuthTests
    {
        private static readonly OAuthClientConfig client = new OAuthClientConfig { ClientId = "1234", ClientSecret = "secret" };

        private static StubHttpHandler tokenEndpoint(Func<Dictionary<string, string>, HttpResponseMessage> respond)
        {
            var handler = new StubHttpHandler();
            handler.On("POST", "/oauth/token", r =>
            {
                string body = r.Content!.ReadAsStringAsync().Result;
                var form = HttpUtility.ParseQueryString(body);
                return respond(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
            });
            return handler;
        }

        [Fact]
        public void AuthorizeUriHasRequiredParameters()
        {
            var uri = new OsuOAuthClient(new HttpClient()).BuildAuthorizeUri(client, "st");
            var q = HttpUtility.ParseQueryString(uri.Query);

            Assert.Equal("https://osu.ppy.sh/oauth/authorize", uri.GetLeftPart(UriPartial.Path));
            Assert.Equal("1234", q["client_id"]);
            Assert.Equal("http://localhost:7270/callback", q["redirect_uri"]);
            Assert.Equal("code", q["response_type"]);
            Assert.Equal("st", q["state"]);
            Assert.Equal("identify public chat.read chat.write chat.write_manage multiplayer.write_manage", q["scope"]);
        }

        [Fact]
        public async Task ExchangeAndRefresh()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));
            Dictionary<string, string>? lastForm = null;

            var handler = tokenEndpoint(form =>
            {
                lastForm = form;
                return StubHttpHandler.Json(new { token_type = "Bearer", expires_in = 86400, access_token = $"at-{form["grant_type"]}", refresh_token = "rt2" });
            });

            var oauth = new OsuOAuthClient(new HttpClient(handler), time: time);
            var tokens = await oauth.ExchangeCodeAsync(client, "the-code");

            Assert.Equal("at-authorization_code", tokens.AccessToken);
            Assert.Equal(time.GetUtcNow().AddDays(1), tokens.ExpiresAt);
            Assert.Equal("the-code", lastForm!["code"]);
            Assert.Equal("secret", lastForm["client_secret"]);
            Assert.Equal("http://localhost:7270/callback", lastForm["redirect_uri"]);

            var refreshed = await oauth.RefreshAsync(client, "rt");
            Assert.Equal("at-refresh_token", refreshed.AccessToken);
            Assert.Equal("rt", lastForm["refresh_token"]);
        }

        [Fact]
        public async Task RejectedRefreshRequiresLogin()
        {
            var handler = tokenEndpoint(_ => StubHttpHandler.Json(new { error = "invalid_grant", error_description = "revoked" }, HttpStatusCode.BadRequest));
            var oauth = new OsuOAuthClient(new HttpClient(handler));

            var ex = await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => oauth.RefreshAsync(client, "rt"));
            Assert.Equal("invalid_grant", ex.Error);
        }

        [Fact]
        public async Task ProfilePersistsAndRefreshesNearExpiry()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));
            int refreshes = 0;
            var handler = tokenEndpoint(_ =>
            {
                refreshes++;
                return StubHttpHandler.Json(new { expires_in = 86400, access_token = $"at{refreshes}", refresh_token = $"rt{refreshes}" });
            });

            var store = new InMemorySecretStore();
            var oauth = new OsuOAuthClient(new HttpClient(handler), time: time);

            var profile = await RefereeProfile.CreateAsync("p", client, store, oauth, time);
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => profile.GetAccessTokenAsync());

            // simulate a completed login by storing tokens through a code exchange.
            var tokens = await oauth.ExchangeCodeAsync(client, "c");
            await store.SetAsync("refereehubapi/p/tokens", System.Text.Json.JsonSerializer.Serialize(tokens, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

            var loaded = (await RefereeProfile.LoadAsync("p", store, oauth, time))!;
            Assert.Equal("secret", loaded.Client.ClientSecret);
            Assert.Equal("at1", await loaded.GetAccessTokenAsync());

            time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(56));
            Assert.Equal("at2", await loaded.GetAccessTokenAsync());

            var reloaded = (await RefereeProfile.LoadAsync("p", store, oauth, time))!;
            Assert.Equal("at2", await reloaded.GetAccessTokenAsync());
        }

        [Fact]
        public async Task LoopbackListenerReturnsCodeForMatchingState()
        {
            int port = freePort();
            using var listener = new LoopbackCallbackListener(port);
            listener.Start();

            var wait = listener.WaitForCodeAsync("good", CancellationToken.None);

            using var http = new HttpClient();
            var wrong = await http.GetAsync($"http://localhost:{port}/callback?code=x&state=bad");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.False(wait.IsCompleted);

            var ok = await http.GetAsync($"http://localhost:{port}/callback?code=abc&state=good");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("abc", await wait.WaitAsync(Wait.DEFAULT));
        }

        [Fact]
        public async Task LoopbackListenerSurfacesDenial()
        {
            int port = freePort();
            using var listener = new LoopbackCallbackListener(port);
            listener.Start();

            var wait = listener.WaitForCodeAsync("s", CancellationToken.None);
            using var http = new HttpClient();
            await http.GetAsync($"http://localhost:{port}/callback?error=access_denied&state=s");

            var ex = await Assert.ThrowsAsync<OAuthException>(() => wait.WaitAsync(Wait.DEFAULT));
            Assert.Equal("access_denied", ex.Error);
        }

        private static int freePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
