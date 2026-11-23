using System.Collections.Concurrent;
using System.Text.Json;
using RefereeHubApi.Api;
using RefereeHubApi.Hub.Models;

namespace RefereeHubApi.Beatmaps
{
    // StarRating and MaxCombo come from osu-web (never computed locally); Modded is computed locally.
    public sealed record BeatmapInfo(
        ApiBeatmap Beatmap,
        int RulesetId,
        IReadOnlyList<Mod> Mods,
        double StarRating,
        int? MaxCombo,
        BeatmapDifficulty Original,
        BeatmapDifficulty Modded)
    {
        public bool DifficultyAdjusted => RulesetId == ModdedDifficulty.OSU_RULESET_ID;
    }

    public class BeatmapNotFoundException : Exception
    {
        public int BeatmapId { get; }

        public BeatmapNotFoundException(int beatmapId)
            : base($"Beatmap {beatmapId} not found.")
        {
            BeatmapId = beatmapId;
        }
    }

    // beatmap metadata cached by id, attributes by (beatmap, ruleset, mods). Results are kept for the session;
    // misses are remembered for a minute, so nothing is requested more than once a minute.
    public sealed class BeatmapCatalog
    {
        private readonly OsuApiClient api;
        private readonly TimeProvider time;
        private readonly ConcurrentDictionary<int, ApiBeatmap> beatmaps = new ConcurrentDictionary<int, ApiBeatmap>();
        private readonly ConcurrentDictionary<int, DateTimeOffset> missing = new ConcurrentDictionary<int, DateTimeOffset>();
        private readonly ConcurrentDictionary<string, Task<ApiDifficultyAttributes>> attributes = new ConcurrentDictionary<string, Task<ApiDifficultyAttributes>>();
        private readonly SemaphoreSlim fetchLock = new SemaphoreSlim(1, 1);

        public BeatmapCatalog(OsuApiClient api, TimeProvider? time = null)
        {
            this.api = api;
            this.time = time ?? TimeProvider.System;
        }

        public async Task<ApiBeatmap?> GetAsync(int beatmapId, CancellationToken ct = default)
        {
            var found = await GetManyAsync([beatmapId], ct).ConfigureAwait(false);
            return found.GetValueOrDefault(beatmapId);
        }

        // uncached ids go out in batches of 50 (ids[]), e.g. a whole mappool at once.
        public async Task<IReadOnlyDictionary<int, ApiBeatmap>> GetManyAsync(IEnumerable<int> beatmapIds, CancellationToken ct = default)
        {
            var wanted = beatmapIds.Distinct().ToArray();

            if (wanted.Any(needsFetch))
            {
                await fetchLock.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    var toFetch = wanted.Where(needsFetch).ToArray();

                    if (toFetch.Length == 1)
                    {
                        var single = await api.GetBeatmapAsync(toFetch[0], ct).ConfigureAwait(false);
                        remember(toFetch, single == null ? [] : [single]);
                    }
                    else
                    {
                        foreach (var chunk in toFetch.Chunk(OsuApiClient.MAX_IDS_PER_REQUEST))
                            remember(chunk, await api.GetBeatmapsAsync(chunk, ct).ConfigureAwait(false));
                    }
                }
                finally
                {
                    fetchLock.Release();
                }
            }

            var result = new Dictionary<int, ApiBeatmap>();

            foreach (int id in wanted)
            {
                if (beatmaps.TryGetValue(id, out var b))
                    result[id] = b;
            }

            return result;
        }

        private void remember(IEnumerable<int> requested, IEnumerable<ApiBeatmap> received)
        {
            foreach (var b in received)
                beatmaps[b.Id] = b;

            var now = time.GetUtcNow();

            foreach (int id in requested.Where(i => !beatmaps.ContainsKey(i)))
                missing[id] = now;
        }

        private bool needsFetch(int id)
            => !beatmaps.ContainsKey(id) && !(missing.TryGetValue(id, out var at) && time.GetUtcNow() - at < TimeSpan.FromMinutes(1));

        public Task<ApiDifficultyAttributes> GetAttributesAsync(int beatmapId, int rulesetId, IEnumerable<Mod> mods, CancellationToken ct = default)
        {
            var list = mods.ToArray();
            string key = AttributesKey(beatmapId, rulesetId, list);

            var task = attributes.GetOrAdd(key, _ => fetchAttributes(beatmapId, rulesetId, list));

            // failed requests aren't cached.
            if (task.IsFaulted || task.IsCanceled)
            {
                attributes.TryRemove(new KeyValuePair<string, Task<ApiDifficultyAttributes>>(key, task));
                task = attributes.GetOrAdd(key, _ => fetchAttributes(beatmapId, rulesetId, list));
            }

            return task.WaitAsync(ct);
        }

        private async Task<ApiDifficultyAttributes> fetchAttributes(int beatmapId, int rulesetId, Mod[] mods)
        {
            await Task.Yield();
            return await api.GetBeatmapAttributesAsync(beatmapId, rulesetId, mods).ConfigureAwait(false);
        }

        // metadata + star rating with the item's required mods + locally adjusted stats.
        public Task<BeatmapInfo> DescribeAsync(PlaylistItem item, CancellationToken ct = default)
            => DescribeAsync(item.BeatmapId, item.RulesetId, item.RequiredMods, ct);

        public async Task<BeatmapInfo> DescribeAsync(int beatmapId, int rulesetId, IEnumerable<Mod> mods, CancellationToken ct = default)
        {
            var list = mods.ToArray();
            var beatmap = await GetAsync(beatmapId, ct).ConfigureAwait(false) ?? throw new BeatmapNotFoundException(beatmapId);

            double starRating;
            int? maxCombo;

            // the beatmap's own rating is already the server's number for nomod on its own ruleset; saves a request.
            if (list.Length == 0 && rulesetId == beatmap.RulesetId)
            {
                starRating = beatmap.DifficultyRating;
                maxCombo = beatmap.MaxCombo;
            }
            else
            {
                var attrs = await GetAttributesAsync(beatmapId, rulesetId, list, ct).ConfigureAwait(false);
                starRating = attrs.StarRating;
                maxCombo = attrs.MaxCombo;
            }

            var original = new BeatmapDifficulty(beatmap.CircleSize, beatmap.ApproachRate, beatmap.OverallDifficulty, beatmap.Bpm, beatmap.TotalLength, beatmap.HitLength);
            return new BeatmapInfo(beatmap, rulesetId, list, starRating, maxCombo, original, ModdedDifficulty.Apply(original, list, rulesetId));
        }

        // order of mods and of settings doesn't matter; acronyms are case-insensitive.
        public static string AttributesKey(int beatmapId, int rulesetId, IEnumerable<Mod> mods)
        {
            var parts = mods
                        .OrderBy(m => m.Acronym.ToUpperInvariant(), StringComparer.Ordinal)
                        .Select(m =>
                        {
                            string settings = m.Settings == null
                                ? string.Empty
                                : string.Join(",", m.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={JsonSerializer.Serialize(kv.Value)}"));
                            return $"{m.Acronym.ToUpperInvariant()}({settings})";
                        });

            return $"{beatmapId}/{rulesetId}/{string.Join("+", parts)}";
        }
    }
}
