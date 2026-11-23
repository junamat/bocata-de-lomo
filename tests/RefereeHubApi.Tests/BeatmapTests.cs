using System.Text.Json;
using RefereeHubApi.Api;
using RefereeHubApi.Beatmaps;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class ModdedDifficultyTests
    {
        private static readonly BeatmapDifficulty map = new BeatmapDifficulty(CircleSize: 4, ApproachRate: 9, OverallDifficulty: 8, Bpm: 180, TotalLength: 120, HitLength: 110);

        private static BeatmapDifficulty apply(params string[] acronyms) => ModdedDifficulty.Apply(map, acronyms.Select(a => new Mod(a)));

        private static Mod withSettings(string acronym, string json)
            => new Mod(acronym, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(kv => kv.Key, kv => (object)kv.Value));

        private const int precision = 4;

        [Fact]
        public void NoModsChangesNothing() => Assert.Equal(map, apply());

        [Fact]
        public void HardRockScalesAndCapsAtTen()
        {
            var d = apply("HR");
            Assert.Equal(5.2, d.CircleSize, precision);
            Assert.Equal(10, d.ApproachRate, precision);
            Assert.Equal(10, d.OverallDifficulty, precision);
        }

        [Fact]
        public void EasyHalves()
        {
            var d = apply("EZ");
            Assert.Equal(2, d.CircleSize, precision);
            Assert.Equal(4.5, d.ApproachRate, precision);
            Assert.Equal(4, d.OverallDifficulty, precision);
        }

        [Fact]
        public void DoubleTimeGoesAboveTenWithoutClamp()
        {
            var d = apply("DT");
            Assert.Equal(10.3333, d.ApproachRate, precision);
            Assert.Equal(9.7778, d.OverallDifficulty, precision);
            Assert.Equal(4, d.CircleSize, precision);
            Assert.Equal(270, d.Bpm, precision);
            Assert.Equal(80, d.TotalLength, precision);
            Assert.Equal(apply("NC"), d);
        }

        [Fact]
        public void HardRockAppliesBeforeRate()
        {
            var d = apply("HR", "DT");
            Assert.Equal(11, d.ApproachRate, precision);
            Assert.Equal(11.1111, d.OverallDifficulty, precision);
            Assert.Equal(d, apply("DT", "HR"));
        }

        [Fact]
        public void HalfTimeGoesBelowZeroWithoutClamp()
        {
            var low = ModdedDifficulty.Apply(map with { ApproachRate = 1 }, [new Mod("HT")]);
            Assert.Equal(-3.6667, low.ApproachRate, precision);
            Assert.Equal(apply("DC"), apply("HT"));
        }

        [Fact]
        public void EasyHalfTimeUsesLowApproachBranch()
        {
            var d = ModdedDifficulty.Apply(map with { ApproachRate = 8 }, [new Mod("EZ"), new Mod("HT")]);
            Assert.Equal(0.3333, d.ApproachRate, precision);
        }

        [Fact]
        public void SpeedChangeSettingOverridesDefaultRate()
        {
            var d = ModdedDifficulty.Apply(map, [withSettings("DT", """{"speed_change":1.2}""")]);
            Assert.Equal(9.6667, d.ApproachRate, precision);
            Assert.Equal(216, d.Bpm, precision);
            Assert.Equal(1.2, ModdedDifficulty.RateOf([withSettings("DT", """{"speed_change":1.2}""")]), precision);
        }

        [Fact]
        public void DifficultyAdjustReplacesThenRateApplies()
        {
            var d = ModdedDifficulty.Apply(map, [withSettings("DA", """{"approach_rate":9.5,"circle_size":5}"""), new Mod("DT")]);
            Assert.Equal(5, d.CircleSize, precision);
            Assert.Equal(10.6667, d.ApproachRate, precision);
            Assert.Equal(9.7778, d.OverallDifficulty, precision);
        }

        [Fact]
        public void OtherRulesetsOnlyChangeBpmAndLength()
        {
            var d = ModdedDifficulty.Apply(map, [new Mod("HR"), new Mod("DT")], rulesetId: 1);
            Assert.Equal(map with { Bpm = 270, TotalLength = 80, HitLength = 110 / 1.5 }, d);
        }

        [Fact]
        public void ConversionsAreContinuousAndInverse()
        {
            Assert.Equal(1200, ModdedDifficulty.ApproachRateToPreempt(5));
            Assert.Equal(450, ModdedDifficulty.ApproachRateToPreempt(10));
            Assert.Equal(1800, ModdedDifficulty.ApproachRateToPreempt(0));

            foreach (double ar in new[] { -2, 0, 3.3, 5, 7.5, 10, 11 })
                Assert.Equal(ar, ModdedDifficulty.PreemptToApproachRate(ModdedDifficulty.ApproachRateToPreempt(ar)), precision);

            Assert.Equal(20, ModdedDifficulty.OverallDifficultyToGreatWindow(10));
            Assert.Equal(19.5, ModdedDifficulty.GameplayWindow(20.7));
        }
    }

    public class BeatmapCatalogTests
    {
        private readonly StubHttpHandler http = new StubHttpHandler();

        private static object beatmap(int id, int mode = 0) => new
        {
            id,
            beatmapset_id = 1,
            beatmapset = new { id = 1, artist = "Artist", title = "Title", creator = "Mapper" },
            version = "Insane",
            difficulty_rating = 5.25,
            total_length = 120,
            hit_length = 110,
            bpm = 180,
            cs = 4,
            ar = 9,
            accuracy = 8,
            drain = 6,
            mode = "osu",
            mode_int = mode,
            max_combo = 900,
        };

        private BeatmapCatalog create(out OsuApiClient api)
        {
            api = new OsuApiClient(new HttpClient(http), new FakeCredentials(), new Uri("http://api.test/api/v2/"), requestsPerMinute: 10_000);
            return new BeatmapCatalog(api);
        }

        [Fact]
        public async Task DescribeUsesAttributesForModsAndCachesBoth()
        {
            http.OnJson("GET", "/api/v2/beatmaps/75", _ => beatmap(75));
            http.OnJson("POST", "/api/v2/beatmaps/75/attributes", _ => new { attributes = new { star_rating = 7.1, max_combo = 900, aim_difficulty = 3.2 } });
            var catalog = create(out var api);
            using var _ = api;

            var info = await catalog.DescribeAsync(new PlaylistItem { BeatmapId = 75, RulesetId = 0, RequiredMods = [new Mod("HD"), new Mod("DT")] });
            var again = await catalog.DescribeAsync(75, 0, [new Mod("dt"), new Mod("HD")]);

            Assert.Equal(7.1, info.StarRating);
            Assert.Equal(again.StarRating, info.StarRating);
            Assert.Equal(10.3333, info.Modded.ApproachRate, 4);
            Assert.Equal(9, info.Original.ApproachRate);
            Assert.Equal("Artist - Title [Insane] (Mapper)", info.Beatmap.ToString());
            Assert.Equal(1, http.Count("GET", "/api/v2/beatmaps/75"));
            Assert.Equal(1, http.Count("POST", "/api/v2/beatmaps/75/attributes"));

            string body = http.Bodies.Single(b => b != null && b.Contains("ruleset_id"))!;
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(0, doc.RootElement.GetProperty("ruleset_id").GetInt32());
            Assert.Equal(["HD", "DT"], doc.RootElement.GetProperty("mods").EnumerateArray().Select(m => m.GetProperty("acronym").GetString()));
        }

        [Fact]
        public async Task NomodOnOwnRulesetSkipsAttributes()
        {
            http.OnJson("GET", "/api/v2/beatmaps/75", _ => beatmap(75));
            var catalog = create(out var api);
            using var _ = api;

            var info = await catalog.DescribeAsync(75, 0, []);

            Assert.Equal(5.25, info.StarRating);
            Assert.Equal(0, http.Count("POST", "/api/v2/beatmaps/75/attributes"));
        }

        [Fact]
        public async Task ManyBeatmapsAreBatched()
        {
            http.OnJson("GET", "/api/v2/beatmaps", r => new
            {
                beatmaps = r.RequestUri!.Query.TrimStart('?').Split('&').Select(p => beatmap(int.Parse(Uri.UnescapeDataString(p).Split('=')[1]))).ToArray(),
            });
            var catalog = create(out var api);
            using var _ = api;

            var found = await catalog.GetManyAsync(Enumerable.Range(1, 60));
            await catalog.GetAsync(30);

            Assert.Equal(60, found.Count);
            Assert.Equal(2, http.Requests.Count);
        }

        [Fact]
        public void AttributesKeyIgnoresOrderAndSettingsOrder()
        {
            var a = new Mod("DA", new Dictionary<string, object> { ["approach_rate"] = 9.0, ["circle_size"] = 4.0 });
            var b = new Mod("da", new Dictionary<string, object> { ["circle_size"] = 4.0, ["approach_rate"] = 9.0 });

            Assert.Equal(BeatmapCatalog.AttributesKey(1, 0, [new Mod("HD"), a]), BeatmapCatalog.AttributesKey(1, 0, [b, new Mod("hd")]));
            Assert.NotEqual(BeatmapCatalog.AttributesKey(1, 0, [new Mod("HD")]), BeatmapCatalog.AttributesKey(1, 1, [new Mod("HD")]));
        }

        [Fact]
        public async Task UnknownBeatmapThrows()
        {
            var catalog = create(out var api);
            using var _ = api;

            await Assert.ThrowsAsync<BeatmapNotFoundException>(() => catalog.DescribeAsync(1, 0, []));
            Assert.Null(await catalog.GetAsync(1));
            Assert.Single(http.Requests);
        }
    }
}
