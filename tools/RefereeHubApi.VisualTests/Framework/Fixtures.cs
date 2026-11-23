using RefereeHubApi.Api;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Responses;
using RefereeHubApi.Tests;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.VisualTests.Framework;

// a made-up team match: six players, two refs, an eight-map pool half played.
public static class Fixtures
{
    public const long ROOM_ID = 1284550;

    public const int CHANNEL_ID = 555;

    public sealed record FakePlayer(int Id, string Name, string Country, MatchTeam Team, MatchUserStatus Status);

    public sealed record FakeMap(int Id, string Title, string Artist, string Diff, string Mods, bool Played);

    public static readonly FakePlayer[] Players =
    [
        new FakePlayer(100, "Kirin_", "JP", MatchTeam.Red, MatchUserStatus.Ready),
        new FakePlayer(101, "mossbell", "DE", MatchTeam.Red, MatchUserStatus.Ready),
        new FakePlayer(102, "Tsukuyo", "KR", MatchTeam.Red, MatchUserStatus.Idle),
        new FakePlayer(103, "Orrery", "US", MatchTeam.Blue, MatchUserStatus.Ready),
        new FakePlayer(104, "lumen-k", "PL", MatchTeam.Blue, MatchUserStatus.Ready),
        new FakePlayer(105, "Haruno", "JP", MatchTeam.Blue, MatchUserStatus.Spectating),
    ];

    // can join mid-scene; known to the users API but not in the room at the start.
    public static readonly FakePlayer Substitute = new FakePlayer(106, "velvetfox", "FR", MatchTeam.Blue, MatchUserStatus.Idle);

    public static readonly FakeMap[] Maps =
    [
        new FakeMap(4402213, "Glass Garden", "Mira Vale", "Bloom", "", true),
        new FakeMap(4402214, "Static Bloom", "Neon Choir", "Overgrowth", "HD", true),
        new FakeMap(4402215, "Marble Sky", "Ilse", "Firmament", "HR", true),
        new FakeMap(4402217, "Rush Hour Comet", "Velo", "Perihelion", "DT", true),
        new FakeMap(4411820, "Cyber Inductance", "Kasane Signal", "Overload", "HD", false),
        new FakeMap(4402219, "Weightless", "Aster Field", "Zenith", "HR", false),
        new FakeMap(4402220, "Velocity Bloom", "Neon Choir", "Burst", "DT", false),
        new FakeMap(4398004, "Endless Daybreak", "Aster Field", "Last Light", "", false),
    ];

    public static string NameOf(int userId) => userId switch
    {
        TestIds.SELF => "TestRef",
        TestIds.OTHER_REF => "Strelok",
        _ => Players.Append(Substitute).FirstOrDefault(p => p.Id == userId)?.Name ?? $"user{userId}",
    };

    public static void StubApi(StubHttpHandler api)
    {
        api.OnJson("GET", "/api/v2/me", _ => ApiFixtures.Me);
        api.OnJson("GET", "/api/v2/users", _ => new
        {
            users = Players.Append(Substitute)
                           .Select(p => new { id = p.Id, username = p.Name, country_code = p.Country })
                           .Append(new { id = TestIds.SELF, username = "TestRef", country_code = "ES" })
                           .Append(new { id = TestIds.OTHER_REF, username = "Strelok", country_code = "UA" }),
        });
        api.OnJson("POST", "/api/v2/beatmaps/", _ => new { attributes = new { star_rating = 6.42, max_combo = 1310 } });
        api.OnJson("GET", "/api/v2/beatmaps/", r => Beatmap(int.Parse(r.RequestUri!.AbsolutePath.Split('/').Last())));
        api.OnJson("GET", "/api/v2/beatmaps", _ => new { beatmaps = Maps.Select(m => Beatmap(m.Id)) });
    }

    public static object Beatmap(int id)
    {
        var m = Maps.FirstOrDefault(x => x.Id == id);
        return new
        {
            id,
            beatmapset_id = id / 10,
            beatmapset = new { id = id / 10, artist = m?.Artist ?? "Artist", title = m?.Title ?? "Title", creator = "Rinsé" },
            version = m?.Diff ?? "Insane",
            difficulty_rating = 6.01,
            total_length = 201,
            hit_length = 195,
            bpm = 178,
            cs = 4.2,
            ar = 9.3,
            accuracy = 8.8,
            drain = 5,
            mode = "osu",
            mode_int = 0,
            max_combo = 1310,
        };
    }

    public static RoomJoinedResponse Room(long id) => new RoomJoinedResponse
    {
        RoomId = id,
        ChatChannelId = CHANNEL_ID,
        Name = "TEST: (Team Aurora) vs (Team Nightfall)",
        Password = "k7Q2-mx9f",
        MaxParticipants = 8,
        QueueMode = QueueMode.HostOnly,
        State = new MatchState { Type = MatchType.TeamVersus, Locked = true, Slots = Players.Select(p => (int?)p.Id).Append(null).Append(null).ToArray() },
        Playlist = Maps.Select((m, i) => new PlaylistItem
        {
            PlaylistItemId = i + 1,
            BeatmapId = m.Id,
            Order = i + 1,
            WasPlayed = m.Played,
            RequiredMods = m.Mods.Length == 0 ? [] : [new Mod(m.Mods)],
        }).ToArray(),
        Players = Players.Select(p => new Player
        {
            UserId = p.Id,
            Status = p.Status,
            Team = p.Team,
            UserMods = p.Id == 102 ? [new Mod("HD"), new Mod("NF")] : [new Mod("HD")],
        }).ToArray(),
        Referees = [new Referee { UserId = TestIds.SELF }, new Referee { UserId = TestIds.OTHER_REF }],
    };

    public static ChatMessage Message(long id, int sender, string text) => new ChatMessage
    {
        MessageId = id,
        SenderId = sender,
        ChannelId = CHANNEL_ID,
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(id - 10),
        Content = text,
        Sender = new ApiUser { Id = sender, Username = NameOf(sender) },
    };

    public static MultiplayerScore Score(int user, long total, double acc, string rank, bool passed = true) => new MultiplayerScore
    {
        UserId = user,
        TotalScore = total,
        Accuracy = acc,
        MaxCombo = (int)(total / 650),
        Rank = rank,
        Passed = passed,
        Mods = passed ? [new MultiplayerScoreMod { Acronym = "HD" }] : [new MultiplayerScoreMod { Acronym = "HD" }, new MultiplayerScoreMod { Acronym = "NF" }],
        User = new ApiUser { Id = user, Username = NameOf(user) },
    };

    // the scores endpoint shape for one finished map, Nightfall edging it.
    public static object ScoresJson(params (int User, long Total, double Acc, string Rank, bool Passed)[] scores) => new
    {
        scores = scores.Select((s, i) => new
        {
            id = i + 1,
            user_id = s.User,
            total_score = s.Total,
            accuracy = s.Acc,
            max_combo = (int)(s.Total / 650),
            mods = new[] { new { acronym = "HD" } },
            passed = s.Passed,
            rank = s.Rank,
            user = new { id = s.User, username = NameOf(s.User) },
        }),
        total = scores.Length,
        cursor_string = (string?)null,
    };
}
