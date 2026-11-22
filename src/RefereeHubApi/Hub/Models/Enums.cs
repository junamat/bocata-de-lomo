// Wire models ported from ppy/osu-server-spectator (MIT licence),
// osu.Server.Spectator/Hubs/Referee/Models at commit c64fcb6.

using System.Text.Json.Serialization;

namespace RefereeHubApi.Hub.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter<CountdownType>))]
    public enum CountdownType
    {
        [JsonStringEnumMemberName("match_start")]
        MatchStart,

        [JsonStringEnumMemberName("server_shutting_down")]
        ServerShuttingDown,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<MatchTeam>))]
    public enum MatchTeam
    {
        [JsonStringEnumMemberName("red")]
        Red = 0,

        [JsonStringEnumMemberName("blue")]
        Blue = 1,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<MatchType>))]
    public enum MatchType
    {
        [JsonStringEnumMemberName("head_to_head")]
        HeadToHead = 1,

        [JsonStringEnumMemberName("team_versus")]
        TeamVersus = 2,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<MatchUserStatus>))]
    public enum MatchUserStatus
    {
        [JsonStringEnumMemberName("idle")]
        Idle,

        [JsonStringEnumMemberName("ready")]
        Ready,

        [JsonStringEnumMemberName("playing")]
        Playing,

        [JsonStringEnumMemberName("finished_play")]
        FinishedPlay,

        [JsonStringEnumMemberName("spectating")]
        Spectating,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<QueueMode>))]
    public enum QueueMode
    {
        [JsonStringEnumMemberName("host_only")]
        HostOnly = 0,

        [JsonStringEnumMemberName("all_players")]
        AllPlayers = 1,

        [JsonStringEnumMemberName("all_players_round_robin")]
        AllPlayersRoundRobin = 2,
    }
}
