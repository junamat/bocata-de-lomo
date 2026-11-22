// Wire models ported from ppy/osu-server-spectator (MIT licence),
// osu.Server.Spectator/Hubs/Referee/Models/Responses at commit c64fcb6.

using System.Text.Json.Serialization;

namespace RefereeHubApi.Hub.Models.Responses
{
    public class RoomJoinedResponse
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("chat_channel_id")]
        public int ChatChannelId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        // 0 = no limit.
        [JsonPropertyName("max_participants")]
        public byte MaxParticipants { get; set; }

        // Upstream has no JsonPropertyName here; SignalR's default camelCase policy sends "state".
        [JsonPropertyName("state")]
        public MatchState State { get; set; } = new MatchState();

        [JsonPropertyName("queue_mode")]
        public QueueMode QueueMode { get; set; }

        [JsonPropertyName("playlist")]
        public PlaylistItem[] Playlist { get; set; } = [];

        [JsonPropertyName("players")]
        public Player[] Players { get; set; } = [];

        [JsonPropertyName("referees")]
        public Referee[] Referees { get; set; } = [];
    }

    public class ListRoomsResponse
    {
        [JsonPropertyName("room_ids")]
        public long[] RoomIds { get; set; } = [];
    }
}
