using System.Globalization;
using System.Text;
using RefereeHubApi.Api;
using RefereeHubApi.Hub.Models;

namespace RefereeHubApi.Logs
{
    public enum ChatLogTimeZone
    {
        Utc,

        Local,
    }

    public sealed class ChatLogFormat
    {
        public ChatLogTimeZone TimeZone { get; set; } = ChatLogTimeZone.Utc;

        public bool IncludeResults { get; set; } = true;
    }

    // plain-text transcript: a header (channel, start, duration), then "hh:mm:ss user: text" lines.
    public static class ChatLogFormatter
    {
        private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

        // usernames: for messages without an embedded sender; falls back to the user id.
        public static string Format(ChatTranscript transcript, ChatLogFormat? format = null, Func<int, string?>? usernames = null, TimeZoneInfo? localZone = null)
        {
            format ??= new ChatLogFormat();
            var zone = format.TimeZone == ChatLogTimeZone.Utc ? TimeZoneInfo.Utc : localZone ?? TimeZoneInfo.Local;

            var entries = new List<(DateTimeOffset At, long Order, string Text)>();

            foreach (var m in transcript.Messages)
            {
                string name = m.Sender?.Username ?? usernames?.Invoke(m.SenderId) ?? m.SenderId.ToString(invariant);
                entries.Add((m.Timestamp, m.MessageId, m.IsAction ? $"* {name} {m.Content}" : $"{name}: {m.Content}"));
            }

            if (format.IncludeResults)
            {
                foreach (var r in transcript.Results)
                {
                    foreach (string line in resultLines(r, usernames))
                        entries.Add((r.At, long.MaxValue, line));
                }
            }

            // stable: results keep their line order after messages at the same instant.
            entries = entries.Select((e, i) => (e, i)).OrderBy(x => x.e.At).ThenBy(x => x.e.Order).ThenBy(x => x.i).Select(x => x.e).ToList();

            var start = entries.Count > 0 && entries[0].At < transcript.StartedAt ? entries[0].At : transcript.StartedAt;
            var end = entries.Count > 0 ? entries.Max(e => e.At) : start;
            if (end < start)
                end = start;

            var sb = new StringBuilder();
            sb.Append("Channel: ").Append(transcript.Title).Append(" (").Append(transcript.ChannelId.ToString(invariant)).Append(')').Append('\n');
            sb.Append("Started: ").Append(TimeZoneInfo.ConvertTime(start, zone).ToString("yyyy-MM-dd HH:mm:ss", invariant)).Append(' ').Append(zoneLabel(zone, start)).Append('\n');
            sb.Append("Duration: ").Append(duration(end - start)).Append('\n');
            sb.Append('\n');

            foreach (var e in entries)
                sb.Append(TimeZoneInfo.ConvertTime(e.At, zone).ToString("HH:mm:ss", invariant)).Append(' ').Append(e.Text).Append('\n');

            return sb.ToString();
        }

        private static IEnumerable<string> resultLines(TranscriptResult r, Func<int, string?>? usernames)
        {
            var result = r.Result;
            string map = r.BeatmapId is int id ? $", beatmap {id}" : string.Empty;
            string partial = result.Complete ? string.Empty : $" ({result.Scores.Count}/{result.ExpectedScoreCount} scores)";
            yield return $"-- result: playlist item {result.PlaylistItemId}{map}{partial}";

            var teams = result.Match?.Teams;
            int place = 0;

            foreach (var s in result.Scores.OrderByDescending(s => s.TotalScore))
            {
                string name = s.User?.Username ?? usernames?.Invoke(s.UserId) ?? s.UserId.ToString(invariant);
                string team = teams != null && teams.TryGetValue(s.UserId, out var t) ? $" ({teamName(t)})" : string.Empty;
                string mods = s.Mods.Length > 0 ? $" +{string.Join("", s.Mods.Select(m => m.Acronym))}" : string.Empty;
                string failed = s.Passed ? string.Empty : " FAILED";
                yield return $"--   {++place}. {name}{team} {s.TotalScore.ToString("N0", invariant)} {(s.Accuracy * 100).ToString("0.00", invariant)}% x{s.MaxCombo}{mods}{failed}";
            }

            if (result.Match is { Type: MatchType.TeamVersus } && teams is { Count: > 0 })
            {
                var totals = result.Scores
                                   .Where(s => teams.ContainsKey(s.UserId))
                                   .GroupBy(s => teams[s.UserId])
                                   .OrderBy(g => g.Key)
                                   .Select(g => $"{teamName(g.Key)} {g.Sum(s => s.TotalScore).ToString("N0", invariant)}");
                yield return $"--   totals: {string.Join(" | ", totals)}";
            }
        }

        private static string teamName(MatchTeam team) => team == MatchTeam.Red ? "red" : "blue";

        private static string duration(TimeSpan d) => $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}";

        private static string zoneLabel(TimeZoneInfo zone, DateTimeOffset at)
        {
            if (zone == TimeZoneInfo.Utc)
                return "UTC";

            var offset = zone.GetUtcOffset(at);
            return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
        }
    }
}
