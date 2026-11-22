using System.Text.Json;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Hub.Models.Responses;

namespace RefereeHubApi.Tests
{
    public class WireFormatTests
    {
        // SignalR's JSON protocol uses camelCase by default; JsonPropertyName wins where present.
        private static readonly JsonSerializerOptions signalr_like = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        [Fact]
        public void RoomJoinedResponseDeserializes()
        {
            const string json = """
                {"room_id":123,"chat_channel_id":456,"name":"TEST: (A) vs (B)","password":"p","max_participants":0,
                 "state":{"type":"team_versus","locked":true,"slots":[100,null,101]},
                 "queue_mode":"all_players_round_robin",
                 "playlist":[{"id":9,"ruleset_id":0,"beatmap_id":75,"required_mods":[{"acronym":"DT","settings":{"speed_change":1.3}}],"allowed_mods":[],"freestyle":false,"was_played":false,"order":0}],
                 "players":[{"user_id":100,"status":"finished_play","style":{"ruleset_id":null,"beatmap_id":null},"mods":[{"acronym":"HD"}],"team":"blue"}],
                 "referees":[{"user_id":2}]}
                """;

            var r = JsonSerializer.Deserialize<RoomJoinedResponse>(json, signalr_like)!;

            Assert.Equal(123, r.RoomId);
            Assert.Equal(456, r.ChatChannelId);
            Assert.Equal(MatchType.TeamVersus, r.State.Type);
            Assert.True(r.State.Locked);
            Assert.Equal([100, null, 101], r.State.Slots!);
            Assert.Equal(QueueMode.AllPlayersRoundRobin, r.QueueMode);
            Assert.Equal("DT", r.Playlist[0].RequiredMods[0].Acronym);
            Assert.Equal(1.3, ((JsonElement)r.Playlist[0].RequiredMods[0].Settings!["speed_change"]).GetDouble());
            Assert.Equal(MatchUserStatus.FinishedPlay, r.Players[0].Status);
            Assert.Equal(MatchTeam.Blue, r.Players[0].Team);
            Assert.Equal(2, r.Referees[0].UserId);
        }

        [Fact]
        public void EventsDeserialize()
        {
            var started = JsonSerializer.Deserialize<MatchStartedEvent>("""{"room_id":1,"playlist_item_id":9,"type":"team_versus","teams":{"100":"red","101":"blue"},"slots":{"100":0,"101":1}}""", signalr_like)!;
            Assert.Equal(MatchTeam.Blue, started.Teams![101]);
            Assert.Equal(1, started.Slots![101]);

            var countdown = JsonSerializer.Deserialize<CountdownStartedEvent>("""{"room_id":1,"countdown_id":3,"seconds":21600.5,"type":"server_shutting_down"}""", signalr_like)!;
            Assert.Equal(CountdownType.ServerShuttingDown, countdown.Type);
            Assert.Equal(21600.5, countdown.Seconds);

            var kicked = JsonSerializer.Deserialize<UserKickedEvent>("""{"room_id":1,"kicked_user_id":100,"kicking_user_id":2}""", signalr_like)!;
            Assert.Equal(100, kicked.KickedUserId);
        }

        [Fact]
        public void RequestsSerializeSnakeCase()
        {
            string make = JsonSerializer.Serialize(new MakeRoomRequest { BeatmapId = 75, RulesetId = 0, RoomName = "x", MatchType = MatchType.TeamVersus, MaxParticipants = 2 }, signalr_like);
            Assert.Contains("\"beatmap_id\":75", make);
            Assert.Contains("\"type\":\"team_versus\"", make);
            Assert.Contains("\"queue_mode\":\"host_only\"", make);
            Assert.Contains("\"max_participants\":2", make);

            string move = JsonSerializer.Serialize(new MoveUserRequest { UserId = 5, Team = MatchTeam.Red }, signalr_like);
            Assert.Contains("\"team\":\"red\"", move);

            string mods = JsonSerializer.Serialize(new EditCurrentPlaylistItemRequest { RequiredMods = [new Mod("HR")], Freestyle = true }, signalr_like);
            Assert.Contains("\"required_mods\":[{\"acronym\":\"HR\",\"settings\":null}]", mods);
            Assert.Contains("\"freestyle\":true", mods);
        }
    }
}
