// Wire models ported from ppy/osu-server-spectator (MIT licence),
// osu.Server.Spectator/Hubs/Referee/Models at commit c64fcb6.

using System.Text.Json.Serialization;

namespace RefereeHubApi.Hub.Models
{
    public class Mod
    {
        [JsonPropertyName("acronym")]
        public string Acronym { get; set; } = string.Empty;

        // values come back from the hub as JsonElement.
        [JsonPropertyName("settings")]
        public Dictionary<string, object>? Settings { get; set; }

        public Mod()
        {
        }

        public Mod(string acronym, Dictionary<string, object>? settings = null)
        {
            Acronym = acronym;
            Settings = settings;
        }

        public override string ToString() => Acronym;
    }

    public class Style
    {
        [JsonPropertyName("ruleset_id")]
        public int? RulesetId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int? BeatmapId { get; set; }
    }

    public class Player
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("status")]
        public MatchUserStatus Status { get; set; }

        [JsonPropertyName("style")]
        public Style Style { get; set; } = new Style();

        [JsonPropertyName("mods")]
        public Mod[] UserMods { get; set; } = [];

        [JsonPropertyName("team")]
        public MatchTeam? Team { get; set; }
    }

    public class Referee
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }
    }

    public class PlaylistItem
    {
        [JsonPropertyName("id")]
        public long PlaylistItemId { get; set; }

        [JsonPropertyName("ruleset_id")]
        public int RulesetId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int BeatmapId { get; set; }

        [JsonPropertyName("required_mods")]
        public Mod[] RequiredMods { get; set; } = [];

        [JsonPropertyName("allowed_mods")]
        public Mod[] AllowedMods { get; set; } = [];

        [JsonPropertyName("freestyle")]
        public bool Freestyle { get; set; }

        [JsonPropertyName("was_played")]
        public bool WasPlayed { get; set; }

        [JsonPropertyName("order")]
        public int Order { get; set; }
    }

    public class MatchState
    {
        [JsonPropertyName("type")]
        public MatchType Type { get; set; }

        [JsonPropertyName("locked")]
        public bool Locked { get; set; }

        // index is the slot, value is the user in it.
        [JsonPropertyName("slots")]
        public int?[]? Slots { get; set; }
    }
}
