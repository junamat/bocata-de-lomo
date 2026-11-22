// Wire models ported from ppy/osu-server-spectator (MIT licence),
// osu.Server.Spectator/Hubs/Referee/Models/Requests at commit c64fcb6.

using System.Text.Json.Serialization;

namespace RefereeHubApi.Hub.Models.Requests
{
    public class MakeRoomRequest
    {
        public const int MAX_PARTICIPANTS_LIMIT = 16;

        [JsonPropertyName("ruleset_id")]
        public int RulesetId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int BeatmapId { get; set; }

        [JsonPropertyName("name")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public MatchType MatchType { get; set; } = MatchType.HeadToHead;

        [JsonPropertyName("queue_mode")]
        public QueueMode QueueMode { get; set; } = QueueMode.HostOnly;

        // 0 = no limit, otherwise 2-16.
        [JsonPropertyName("max_participants")]
        public byte MaxParticipants { get; set; }
    }

    public class ChangeRoomSettingsRequest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        // "" removes the password.
        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("type")]
        public MatchType? MatchType { get; set; }

        [JsonPropertyName("queue_mode")]
        public QueueMode? QueueMode { get; set; }

        [JsonPropertyName("max_participants")]
        public byte? MaxParticipants { get; set; }
    }

    public abstract class EditPlaylistItemRequestParameters
    {
        [JsonPropertyName("ruleset_id")]
        public int? RulesetId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int? BeatmapId { get; set; }

        [JsonPropertyName("required_mods")]
        public IEnumerable<Mod>? RequiredMods { get; set; }

        [JsonPropertyName("allowed_mods")]
        public IEnumerable<Mod>? AllowedMods { get; set; }

        [JsonPropertyName("freestyle")]
        public bool? Freestyle { get; set; }
    }

    public class EditCurrentPlaylistItemRequest : EditPlaylistItemRequestParameters
    {
    }

    public class EditPlaylistItemRequest : EditPlaylistItemRequestParameters
    {
        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }
    }

    public class AddPlaylistItemRequest
    {
        [JsonPropertyName("ruleset_id")]
        public int RulesetId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int BeatmapId { get; set; }

        [JsonPropertyName("required_mods")]
        public IEnumerable<Mod> RequiredMods { get; set; } = [];

        [JsonPropertyName("allowed_mods")]
        public IEnumerable<Mod> AllowedMods { get; set; } = [];

        [JsonPropertyName("freestyle")]
        public bool Freestyle { get; set; }
    }

    public class RemovePlaylistItemRequest
    {
        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }
    }

    public class MoveUserRequest
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("slot")]
        public byte? Slot { get; set; }

        [JsonPropertyName("team")]
        public MatchTeam? Team { get; set; }
    }

    public class SetLockStateRequest
    {
        [JsonPropertyName("locked")]
        public bool Locked { get; set; }
    }

    public class StartGameplayRequest
    {
        // seconds; null starts immediately.
        [JsonPropertyName("countdown")]
        public int? Countdown { get; set; }
    }

    public class RollRequest
    {
        // 1-100; null uses the server default.
        [JsonPropertyName("max")]
        public uint? Max { get; set; }
    }
}
