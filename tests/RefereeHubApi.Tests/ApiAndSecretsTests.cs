using System.Diagnostics;
using System.Net;
using RefereeHubApi.Api;
using RefereeHubApi.Secrets;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class ApiAndSecretsTests
    {
        [Fact]
        public async Task RateLimiterHoldsRequestsBeyondBudget()
        {
            var http = new StubHttpHandler().OnJson("GET", "/api/v2/me", _ => ApiFixtures.Me);
            using var api = new OsuApiClient(new HttpClient(http), new FakeCredentials(), new Uri("http://api.test/api/v2/"), requestsPerMinute: 2);

            await api.GetMeAsync();
            await api.GetMeAsync();

            var third = api.GetMeAsync();
            await Task.Delay(300);
            Assert.False(third.IsCompleted);
            Assert.Equal(2, http.Requests.Count);
        }

        [Fact]
        public async Task SendsBearerTokenAndRetriesOnce429()
        {
            int calls = 0;
            var http = new StubHttpHandler().On("GET", "/api/v2/me", _ =>
            {
                if (++calls == 1)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                    return r;
                }

                return StubHttpHandler.Json(ApiFixtures.Me);
            });

            using var api = new OsuApiClient(new HttpClient(http), new FakeCredentials { Token = "tok" }, new Uri("http://api.test/api/v2/"));
            var me = await api.GetMeAsync();

            Assert.Equal(TestIds.SELF, me.Id);
            Assert.All(http.Requests, r => Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString()));
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ScoresFollowCursor()
        {
            var http = new StubHttpHandler().On("GET", "/api/v2/rooms/1/playlist/2/scores", r =>
                r.RequestUri!.Query.Contains("cursor_string=")
                    ? StubHttpHandler.Json(new { scores = new[] { new { id = 2, user_id = 200, total_score = 2 } }, total = 2, cursor_string = (string?)null })
                    : StubHttpHandler.Json(new { scores = new[] { new { id = 1, user_id = 100, total_score = 1 } }, total = 2, cursor_string = "abc" }));

            using var api = new OsuApiClient(new HttpClient(http), new FakeCredentials(), new Uri("http://api.test/api/v2/"));
            var scores = await api.GetPlaylistItemScoresAsync(1, 2);

            Assert.Equal([100, 200], scores.Select(s => s.UserId));
        }

        [Fact]
        public async Task ApiErrorsCarryStatus()
        {
            var http = new StubHttpHandler();
            using var api = new OsuApiClient(new HttpClient(http), new FakeCredentials(), new Uri("http://api.test/api/v2/"));

            var ex = await Assert.ThrowsAsync<OsuApiException>(() => api.GetMeAsync());
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        }

        [Fact]
        public void UnsupportedOsFailsExplicitly()
        {
            if (OperatingSystem.IsLinux())
                Assert.IsType<LibsecretSecretStore>(SecretStores.CreateForCurrentOs());
            else
                Assert.Throws<PlatformNotSupportedException>(() => SecretStores.CreateForCurrentOs());
        }

        [Fact]
        public async Task LibsecretRoundTrip()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("REFEREEHUB_TEST_KEYRING") != "1")
                return;

            using var store = new LibsecretSecretStore("RefereeHubApi.Test");
            string key = $"test-{Guid.NewGuid():N}";

            await store.SetAsync(key, "välue with ünicode");
            Assert.Equal("välue with ünicode", await store.GetAsync(key));

            await store.SetAsync(key, "second");
            Assert.Equal("second", await store.GetAsync(key));

            await store.DeleteAsync(key);
            Assert.Null(await store.GetAsync(key));
        }
    }
}
