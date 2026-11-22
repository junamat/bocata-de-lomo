using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefereeHubApi.Api
{
    public sealed class ApiUser
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public sealed class ChatMessage
    {
        [JsonPropertyName("message_id")]
        public long MessageId { get; set; }

        [JsonPropertyName("sender_id")]
        public int SenderId { get; set; }

        [JsonPropertyName("channel_id")]
        public long ChannelId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("is_action")]
        public bool IsAction { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("sender")]
        public ApiUser? Sender { get; set; }
    }

    public sealed class MultiplayerScoreMod
    {
        [JsonPropertyName("acronym")]
        public string Acronym { get; set; } = string.Empty;

        [JsonPropertyName("settings")]
        public Dictionary<string, JsonElement>? Settings { get; set; }
    }

    public sealed class MultiplayerScore
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("total_score")]
        public long TotalScore { get; set; }

        [JsonPropertyName("accuracy")]
        public double Accuracy { get; set; }

        [JsonPropertyName("max_combo")]
        public int MaxCombo { get; set; }

        [JsonPropertyName("mods")]
        public MultiplayerScoreMod[] Mods { get; set; } = [];

        [JsonPropertyName("passed")]
        public bool Passed { get; set; }

        [JsonPropertyName("rank")]
        public string? Rank { get; set; }

        [JsonPropertyName("statistics")]
        public Dictionary<string, int>? Statistics { get; set; }

        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; set; }

        [JsonPropertyName("user")]
        public ApiUser? User { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public sealed class MultiplayerScoresPage
    {
        [JsonPropertyName("scores")]
        public MultiplayerScore[] Scores { get; set; } = [];

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("cursor_string")]
        public string? CursorString { get; set; }
    }
}
