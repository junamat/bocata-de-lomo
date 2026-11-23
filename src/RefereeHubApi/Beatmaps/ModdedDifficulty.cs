using System.Globalization;
using System.Text.Json;
using RefereeHubApi.Hub.Models;

namespace RefereeHubApi.Beatmaps
{
    // lengths in seconds. AR/OD are display values: after a rate change they can go above 10 or below 0.
    public sealed record BeatmapDifficulty(
        double CircleSize,
        double ApproachRate,
        double OverallDifficulty,
        double Bpm,
        double TotalLength,
        double HitLength);

    // mod-adjusted CS/AR/OD/BPM/length, computed locally since the attributes endpoint only returns star rating and combo.
    // Mirrors lazer's OsuRuleset.GetAdjustedDisplayDifficulty: EZ/HR/DA first, then the rate change through
    // approach time and the 300 hit window. Pure functions, no clamping after the rate change.
    public static class ModdedDifficulty
    {
        public const double DOUBLE_TIME_RATE = 1.5;
        public const double HALF_TIME_RATE = 0.75;

        private const double hard_rock_multiplier = 1.4;
        private const double hard_rock_circle_size_multiplier = 1.3;
        private const double easy_multiplier = 0.5;

        public const int OSU_RULESET_ID = 0;

        // CS/AR/OD are only adjusted for the osu! ruleset; for others only BPM and length change with the rate.
        public static BeatmapDifficulty Apply(BeatmapDifficulty original, IEnumerable<Mod> mods, int rulesetId = OSU_RULESET_ID)
        {
            var list = mods as IReadOnlyCollection<Mod> ?? mods.ToArray();
            double rate = RateOf(list);

            double cs = original.CircleSize;
            double ar = original.ApproachRate;
            double od = original.OverallDifficulty;

            if (rulesetId == OSU_RULESET_ID)
            {
                if (has(list, "HR"))
                {
                    cs = Math.Min(cs * hard_rock_circle_size_multiplier, 10);
                    ar = Math.Min(ar * hard_rock_multiplier, 10);
                    od = Math.Min(od * hard_rock_multiplier, 10);
                }

                if (has(list, "EZ"))
                {
                    cs *= easy_multiplier;
                    ar *= easy_multiplier;
                    od *= easy_multiplier;
                }

                if (list.FirstOrDefault(m => is_(m, "DA")) is Mod da)
                {
                    cs = setting(da, "circle_size") ?? cs;
                    ar = setting(da, "approach_rate") ?? ar;
                    od = setting(da, "overall_difficulty") ?? od;
                }

                if (rate != 1)
                {
                    ar = PreemptToApproachRate(ApproachRateToPreempt(ar) / rate);
                    od = GreatWindowToOverallDifficulty(OverallDifficultyToGreatWindow(od) / rate);
                }
            }

            return new BeatmapDifficulty(cs, ar, od, original.Bpm * rate, original.TotalLength / rate, original.HitLength / rate);
        }

        // DT/NC 1.5, HT/DC 0.75, or their speed_change setting. Several rate mods multiply.
        public static double RateOf(IEnumerable<Mod> mods)
        {
            double rate = 1;

            foreach (var mod in mods)
            {
                double? fallback = mod.Acronym.ToUpperInvariant() switch
                {
                    "DT" or "NC" => DOUBLE_TIME_RATE,
                    "HT" or "DC" => HALF_TIME_RATE,
                    _ => null,
                };

                if (fallback != null)
                    rate *= setting(mod, "speed_change") ?? fallback.Value;
            }

            return rate;
        }

        // approach time in ms: 1800 at AR 0, 1200 at AR 5, 450 at AR 10, linear in between and beyond.
        public static double ApproachRateToPreempt(double ar) => ar < 5 ? 1800 - 120 * ar : 1950 - 150 * ar;

        public static double PreemptToApproachRate(double preempt) => preempt > 1200 ? (1800 - preempt) / 120 : (1950 - preempt) / 150;

        // 300 ("great") window in ms: 80 at OD 0, 20 at OD 10.
        public static double OverallDifficultyToGreatWindow(double od) => 80 - 6 * od;

        public static double GreatWindowToOverallDifficulty(double window) => (80 - window) / 6;

        // gameplay hit windows are floor(w) - 0.5; only for judging, never for display.
        public static double GameplayWindow(double window) => Math.Floor(window) - 0.5;

        private static bool is_(Mod mod, string acronym) => string.Equals(mod.Acronym, acronym, StringComparison.OrdinalIgnoreCase);

        private static bool has(IEnumerable<Mod> mods, string acronym) => mods.Any(m => is_(m, acronym));

        internal static double? setting(Mod mod, string key)
        {
            if (mod.Settings == null || !mod.Settings.TryGetValue(key, out object? value) || value == null)
                return null;

            return value switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
                JsonElement { ValueKind: JsonValueKind.String } e when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
                double d => d,
                float f => f,
                decimal m => (double)m,
                int i => i,
                long l => l,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
                _ => null,
            };
        }
    }
}
