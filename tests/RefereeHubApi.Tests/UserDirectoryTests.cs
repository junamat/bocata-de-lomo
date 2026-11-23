using System.Net;
using Microsoft.Extensions.Time.Testing;
using RefereeHubApi.Api;
using RefereeHubApi.Tests.Infrastructure;
using RefereeHubApi.Users;

namespace RefereeHubApi.Tests
{
    public class UserDirectoryTests
    {
        private readonly StubHttpHandler http = new StubHttpHandler();
        private readonly FakeTimeProvider time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-23T12:00:00Z"));

        private UserDirectory create(out OsuApiClient api)
        {
            api = new OsuApiClient(new HttpClient(http), new FakeCredentials(), new Uri("http://api.test/api/v2/"), requestsPerMinute: 10_000);
            return new UserDirectory(api, time);
        }

        [Theory]
        [InlineData("123", 123, null)]
        [InlineData("#123", 123, null)]
        [InlineData("@123", null, "123")]
        [InlineData("Some Player", null, "Some Player")]
        [InlineData(" peppy ", null, "peppy")]
        public void UserRefParses(string text, int? id, string? name)
        {
            var user = UserRef.Parse(text);
            Assert.Equal(id, user.Id);
            Assert.Equal(name, user.Username);
        }

        [Fact]
        public void UserRefRejectsBadId() => Assert.Throws<FormatException>(() => UserRef.Parse("#12a"));

        [Fact]
        public async Task NameLookupIsCachedAndCaseAndSpaceInsensitive()
        {
            http.OnJson("GET", "/api/v2/users/@", _ => new { id = 7, username = "Some_Player" });
            var users = create(out var api);
            using var _ = api;

            Assert.Equal(7, await users.ResolveIdAsync(UserRef.FromName("Some_Player")));
            Assert.Equal(7, await users.ResolveIdAsync(UserRef.FromName("some player")));
            Assert.Equal("Some_Player", (await users.GetByIdAsync(7))!.Username);

            Assert.Single(http.Requests);
            Assert.Contains("key=username", http.Requests.Single().RequestUri!.Query);
        }

        [Fact]
        public async Task ConcurrentLookupsShareOneRequest()
        {
            var gate = new TaskCompletionSource();
            http.On("GET", "/api/v2/users/@", _ =>
            {
                gate.Task.Wait(Wait.DEFAULT);
                return StubHttpHandler.Json(new { id = 7, username = "x" });
            });
            var users = create(out var api);
            using var _ = api;

            var a = users.ResolveIdAsync(UserRef.FromName("x"));
            var b = users.ResolveIdAsync(UserRef.FromName("X"));
            await Wait.Until(() => http.Requests.Count == 1);
            gate.SetResult();

            int[] ids = await Task.WhenAll(a, b);
            Assert.Equal(new[] { 7, 7 }, ids);
            Assert.Single(http.Requests);
        }

        [Fact]
        public async Task MissingNameIsNotAskedAgainWithinAMinute()
        {
            http.On("GET", "/api/v2/users/@", _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            var users = create(out var api);
            using var _ = api;

            await Assert.ThrowsAsync<UserNotFoundException>(() => users.ResolveIdAsync(UserRef.FromName("ghost")));
            await Assert.ThrowsAsync<UserNotFoundException>(() => users.ResolveIdAsync(UserRef.FromName("ghost")));
            Assert.Single(http.Requests);

            time.Advance(TimeSpan.FromSeconds(61));
            await Assert.ThrowsAsync<UserNotFoundException>(() => users.ResolveIdAsync(UserRef.FromName("ghost")));
            Assert.Equal(2, http.Requests.Count);
        }

        [Fact]
        public async Task IdsAreBatchedInFiftiesAndCached()
        {
            http.OnJson("GET", "/api/v2/users", r =>
            {
                var ids = r.RequestUri!.Query.TrimStart('?').Split('&').Select(p => int.Parse(Uri.UnescapeDataString(p).Split('=')[1]));
                // id 3 doesn't exist.
                return new { users = ids.Where(i => i != 3).Select(i => new { id = i, username = $"u{i}" }).ToArray() };
            });
            var users = create(out var api);
            using var _ = api;

            var found = await users.GetByIdsAsync(Enumerable.Range(1, 120));
            Assert.Equal(119, found.Count);
            Assert.Equal(3, http.Requests.Count);

            // all cached, and the missing one is remembered.
            await users.GetByIdsAsync(Enumerable.Range(1, 120));
            Assert.Equal(3, http.Requests.Count);
            Assert.Equal("u60", users.GetCachedUsername(60));
            Assert.Equal(60, await users.ResolveIdAsync(UserRef.FromName("U60")));
            Assert.Equal(3, http.Requests.Count);
        }

        [Fact]
        public async Task IdRefsNeedNoRequest()
        {
            var users = create(out var api);
            using var _ = api;

            Assert.Equal(5, await users.ResolveIdAsync(5));
            Assert.Empty(http.Requests);
        }
    }
}
