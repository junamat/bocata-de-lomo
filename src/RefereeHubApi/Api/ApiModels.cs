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

    public sealed class UsersResponse
    {
        [JsonPropertyName("users")]
        public ApiUser[] Users { get; set; } = [];
    }

    public sealed class ApiBeatmapset
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("artist")]
        public string Artist { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("creator")]
        public string Creator { get; set; } = string.Empty;
    }

    public sealed class ApiBeatmap
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("beatmapset_id")]
        public int BeatmapsetId { get; set; }

        [JsonPropertyName("beatmapset")]
        public ApiBeatmapset? Beatmapset { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("difficulty_rating")]
        public double DifficultyRating { get; set; }

        // seconds
        [JsonPropertyName("total_length")]
        public double TotalLength { get; set; }

        [JsonPropertyName("hit_length")]
        public double HitLength { get; set; }

        [JsonPropertyName("bpm")]
        public double Bpm { get; set; }

        [JsonPropertyName("cs")]
        public double CircleSize { get; set; }

        [JsonPropertyName("ar")]
        public double ApproachRate { get; set; }

        [JsonPropertyName("accuracy")]
        public double OverallDifficulty { get; set; }

        [JsonPropertyName("drain")]
        public double DrainRate { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("mode_int")]
        public int RulesetId { get; set; }

        [JsonPropertyName("max_combo")]
        public int? MaxCombo { get; set; }

        public override string ToString()
            => Beatmapset == null ? $"beatmap {Id} [{Version}]" : $"{Beatmapset.Artist} - {Beatmapset.Title} [{Version}] ({Beatmapset.Creator})";
    }

    public sealed class BeatmapsResponse
    {
        [JsonPropertyName("beatmaps")]
        public ApiBeatmap[] Beatmaps { get; set; } = [];
    }

    public sealed class ApiDifficultyAttributes
    {
        [JsonPropertyName("star_rating")]
        public double StarRating { get; set; }

        [JsonPropertyName("max_combo")]
        public int MaxCombo { get; set; }

        // per-ruleset fields (aim_difficulty, ...).
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public sealed class BeatmapAttributesResponse
    {
        [JsonPropertyName("attributes")]
        public ApiDifficultyAttributes Attributes { get; set; } = new ApiDifficultyAttributes();
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

    public sealed class ApiChatChannel
    {
        [JsonPropertyName("channel_id")]
        public long ChannelId { get; set; }

        // "PM", "MULTIPLAYER", "PUBLIC", ...
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("users")]
        public int[]? Users { get; set; }
    }

    public sealed class NewPrivateChatResponse
    {
        [JsonPropertyName("channel")]
        public ApiChatChannel Channel { get; set; } = new ApiChatChannel();

        [JsonPropertyName("message")]
        public ChatMessage Message { get; set; } = new ChatMessage();
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
