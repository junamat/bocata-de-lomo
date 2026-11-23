using System.Collections.Concurrent;
using RefereeHubApi.Api;

namespace RefereeHubApi.Users
{
    // name <-> id lookups, cached for the session. Found users stay cached; misses are remembered for a minute
    // so the same user is never asked for more than once a minute (API docs call that abusive).
    public sealed class UserDirectory
    {
        public static readonly TimeSpan NEGATIVE_CACHE_TIME = TimeSpan.FromMinutes(1);

        private readonly OsuApiClient api;
        private readonly TimeProvider time;
        private readonly ConcurrentDictionary<int, ApiUser> byId = new ConcurrentDictionary<int, ApiUser>();
        private readonly ConcurrentDictionary<string, int> idByName = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, DateTimeOffset> missingNames = new ConcurrentDictionary<string, DateTimeOffset>();
        private readonly ConcurrentDictionary<int, DateTimeOffset> missingIds = new ConcurrentDictionary<int, DateTimeOffset>();
        private readonly ConcurrentDictionary<string, Task<ApiUser?>> nameLookups = new ConcurrentDictionary<string, Task<ApiUser?>>();
        private readonly SemaphoreSlim batchLock = new SemaphoreSlim(1, 1);

        public UserDirectory(OsuApiClient api, TimeProvider? time = null)
        {
            this.api = api;
            this.time = time ?? TimeProvider.System;
        }

        // osu! treats spaces and underscores in usernames as the same character.
        public static string NormalizeName(string username) => username.Trim().Replace(' ', '_').ToLowerInvariant();

        public void Remember(ApiUser user)
        {
            if (user.Id <= 0 || string.IsNullOrEmpty(user.Username))
                return;

            byId[user.Id] = user;
            idByName[NormalizeName(user.Username)] = user.Id;
            missingIds.TryRemove(user.Id, out _);
            missingNames.TryRemove(NormalizeName(user.Username), out _);
        }

        public ApiUser? GetCached(int userId) => byId.GetValueOrDefault(userId);

        public string? GetCachedUsername(int userId) => byId.TryGetValue(userId, out var u) ? u.Username : null;

        public async Task<int> ResolveIdAsync(UserRef user, CancellationToken ct = default)
        {
            if (user.Id is int id)
                return id;

            var found = await GetByNameAsync(user.Username!, ct).ConfigureAwait(false);
            return found?.Id ?? throw new UserNotFoundException(user.Username!);
        }

        public async Task<ApiUser?> GetByNameAsync(string username, CancellationToken ct = default)
        {
            string key = NormalizeName(username);

            if (idByName.TryGetValue(key, out int id) && byId.TryGetValue(id, out var cached))
                return cached;

            if (missingNames.TryGetValue(key, out var missedAt) && time.GetUtcNow() - missedAt < NEGATIVE_CACHE_TIME)
                return null;

            // concurrent lookups of the same name share one request.
            var lookup = nameLookups.GetOrAdd(key, _ => lookupName(key, username));

            try
            {
                return await lookup.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (lookup.IsCompleted)
                    nameLookups.TryRemove(new KeyValuePair<string, Task<ApiUser?>>(key, lookup));
            }
        }

        private async Task<ApiUser?> lookupName(string key, string username)
        {
            await Task.Yield();

            var user = await api.GetUserByNameAsync(username.Trim()).ConfigureAwait(false);

            if (user == null)
                missingNames[key] = time.GetUtcNow();
            else
                Remember(user);

            return user;
        }

        public async Task<ApiUser?> GetByIdAsync(int userId, CancellationToken ct = default)
        {
            var found = await GetByIdsAsync([userId], ct).ConfigureAwait(false);
            return found.GetValueOrDefault(userId);
        }

        // uncached ids go out in batches of 50 (ids[]).
        public async Task<IReadOnlyDictionary<int, ApiUser>> GetByIdsAsync(IEnumerable<int> userIds, CancellationToken ct = default)
        {
            var wanted = userIds.Distinct().ToArray();

            if (wanted.Any(needsFetch))
            {
                await batchLock.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    // re-check under the lock: another batch may have fetched some of them meanwhile.
                    foreach (var chunk in wanted.Where(needsFetch).Chunk(OsuApiClient.MAX_IDS_PER_REQUEST))
                    {
                        var users = await api.GetUsersAsync(chunk, ct).ConfigureAwait(false);

                        foreach (var u in users)
                            Remember(u);

                        var now = time.GetUtcNow();

                        foreach (int missing in chunk.Where(i => !byId.ContainsKey(i)))
                            missingIds[missing] = now;
                    }
                }
                finally
                {
                    batchLock.Release();
                }
            }

            var result = new Dictionary<int, ApiUser>();

            foreach (int id in wanted)
            {
                if (byId.TryGetValue(id, out var u))
                    result[id] = u;
            }

            return result;
        }

        private bool needsFetch(int id)
            => !byId.ContainsKey(id) && !(missingIds.TryGetValue(id, out var at) && time.GetUtcNow() - at < NEGATIVE_CACHE_TIME);
    }
}
