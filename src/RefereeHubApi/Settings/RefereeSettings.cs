using RefereeHubApi.Highlights;
using RefereeHubApi.Logs;
using RefereeHubApi.Timers;

namespace RefereeHubApi.Settings
{
    // non-secret preferences only. OAuth secrets and tokens live in ISecretStore, never here.
    public sealed class RefereeSettings
    {
        public const int CURRENT_VERSION = 1;

        public int Version { get; set; } = CURRENT_VERSION;

        // key (as the UI names it, e.g. "F1", "Ctrl+S") -> macro name.
        public Dictionary<string, string> Keybinds { get; set; } = new Dictionary<string, string>();

        // macro name -> command lines, run in order.
        public Dictionary<string, List<string>> Macros { get; set; } = new Dictionary<string, List<string>>();

        // trigger (see AlertTriggers) -> sound id. The library never plays anything; the UI maps ids to sounds.
        public Dictionary<string, string> Alerts { get; set; } = new Dictionary<string, string>();

        public HighlightRules Highlights { get; set; } = new HighlightRules();

        public ChatLogFormat ChatLog { get; set; } = new ChatLogFormat();

        public TimerAnnouncement Timer { get; set; } = new TimerAnnouncement();
    }

    public static class AlertTriggers
    {
        public const string MENTION = "highlight.mention";
        public const string REFEREE_CALL = "highlight.referee_call";
        public const string KEYWORD = "highlight.keyword";
        public const string PRIVATE_MESSAGE = "highlight.private_message";
        public const string TIMER_MILESTONE = "timer.milestone";
        public const string TIMER_FINISHED = "timer.finished";
        public const string MATCH_COMPLETED = "match.completed";
        public const string SERVER_SHUTTING_DOWN = "server.shutting_down";
        public const string PLAYER_JOINED = "room.player_joined";

        public static readonly IReadOnlyList<string> ALL =
        [
            MENTION, REFEREE_CALL, KEYWORD, PRIVATE_MESSAGE, TIMER_MILESTONE, TIMER_FINISHED, MATCH_COMPLETED, SERVER_SHUTTING_DOWN, PLAYER_JOINED,
        ];

        public static IEnumerable<string> ForHighlight(HighlightKind kinds)
        {
            if (kinds.HasFlag(HighlightKind.Mention))
                yield return MENTION;
            if (kinds.HasFlag(HighlightKind.RefereeCall))
                yield return REFEREE_CALL;
            if (kinds.HasFlag(HighlightKind.Keyword))
                yield return KEYWORD;
            if (kinds.HasFlag(HighlightKind.PrivateMessage))
                yield return PRIVATE_MESSAGE;
        }

        // the sound ids configured for a highlight, most specific trigger first, no duplicates.
        public static IReadOnlyList<string> SoundsFor(RefereeSettings settings, HighlightKind kinds)
            => ForHighlight(kinds).Select(t => settings.Alerts.GetValueOrDefault(t)).OfType<string>().Distinct().ToArray();
    }
}
