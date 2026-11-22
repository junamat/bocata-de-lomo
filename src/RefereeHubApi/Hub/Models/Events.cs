// Wire models ported from ppy/osu-server-spectator (MIT licence),
// osu.Server.Spectator/Hubs/Referee/Models/Events at commit c64fcb6.

using System.Text.Json.Serialization;

namespace RefereeHubApi.Hub.Models.Events
{
    public interface IRoomEvent
    {
        long RoomId { get; }
    }

    public class UserJoinedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public long UserId { get; set; }
    }

    public class UserLeftEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public long UserId { get; set; }
    }

    public class UserKickedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("kicked_user_id")]
        public long KickedUserId { get; set; }

        [JsonPropertyName("kicking_user_id")]
        public long KickingUserId { get; set; }
    }

    public class UserBannedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("banned_user_id")]
        public long BannedUserId { get; set; }

        [JsonPropertyName("banning_user_id")]
        public long BanningUserId { get; set; }
    }

    public class RefereeAddedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }
    }

    public class RefereeRemovedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }
    }

    // only sent to the user being added.
    public class RefereeInvitedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }
    }

    public class RoomSettingsChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public MatchType Type { get; set; }

        [JsonPropertyName("queue_mode")]
        public QueueMode QueueMode { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }

        [JsonPropertyName("max_participants")]
        public byte? MaxParticipants { get; set; }
    }

    public class MatchStateChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("state")]
        public MatchState State { get; set; } = new MatchState();
    }

    public class PlaylistItemAddedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item")]
        public PlaylistItem PlaylistItem { get; set; } = new PlaylistItem();
    }

    public class PlaylistItemChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item")]
        public PlaylistItem PlaylistItem { get; set; } = new PlaylistItem();
    }

    public class PlaylistItemRemovedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }
    }

    public class RollCompletedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("max")]
        public uint Max { get; set; }

        [JsonPropertyName("result")]
        public uint Result { get; set; }
    }

    public class UserStatusChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("status")]
        public MatchUserStatus Status { get; set; }
    }

    public class UserModsChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("mods")]
        public Mod[] UserMods { get; set; } = [];
    }

    public class UserStyleChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int? BeatmapId { get; set; }

        [JsonPropertyName("ruleset_id")]
        public int? RulesetId { get; set; }
    }

    public class UserTeamChangedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("team")]
        public MatchTeam? Team { get; set; }
    }

    public class CountdownStartedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("countdown_id")]
        public long CountdownId { get; set; }

        [JsonPropertyName("seconds")]
        public double Seconds { get; set; }

        [JsonPropertyName("type")]
        public CountdownType Type { get; set; }
    }

    public class CountdownStoppedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("countdown_id")]
        public long CountdownId { get; set; }

        [JsonPropertyName("type")]
        public CountdownType Type { get; set; }
    }

    public class MatchStartedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }

        [JsonPropertyName("type")]
        public MatchType MatchType { get; set; }

        // both keyed by user id.
        [JsonPropertyName("teams")]
        public Dictionary<int, MatchTeam>? Teams { get; set; }

        [JsonPropertyName("slots")]
        public Dictionary<int, byte>? Slots { get; set; }
    }

    public class MatchAbortedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }
    }

    public class MatchCompletedEvent : IRoomEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }
    }
}
