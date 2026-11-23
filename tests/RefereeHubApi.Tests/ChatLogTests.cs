using System.Collections.Immutable;
using RefereeHubApi.Api;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Logs;
using RefereeHubApi.Session;
using RefereeHubApi.State;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class ChatLogTests
    {
        private static readonly DateTimeOffset t0 = DateTimeOffset.Parse("2026-11-23T18:00:00Z");

        private static ChatMessage msg(long id, int secondsIn, string content, int sender = TestIds.PLAYER_A, string? name = "PlayerA", bool action = false) => new ChatMessage
        {
            MessageId = id,
            SenderId = sender,
            ChannelId = 555,
            Timestamp = t0.AddSeconds(secondsIn),
            Content = content,
            IsAction = action,
            Sender = name == null ? null : new ApiUser { Id = sender, Username = name },
        };

        private static ChatTranscript transcript()
        {
            var t = new ChatTranscript(555, "#mp_1000 TEST: (A) vs (B)", t0);
            t.Add(msg(2, 65, "waves", action: true));
            t.Add(msg(1, 5, "hello"));
            t.Add(msg(1, 5, "hello"));
            t.Add(msg(3, 3725, "gg", sender: TestIds.PLAYER_B, name: null));
            return t;
        }

        [Fact]
        public void FormatsHeaderAndLinesInUtc()
        {
            string log = ChatLogFormatter.Format(transcript(), new ChatLogFormat { TimeZone = ChatLogTimeZone.Utc }, id => id == TestIds.PLAYER_B ? "PlayerB" : null);

            Assert.Equal(
                """
                Channel: #mp_1000 TEST: (A) vs (B) (555)
                Started: 2026-11-23 18:00:00 UTC
                Duration: 01:02:05

                18:00:05 PlayerA: hello
                18:01:05 * PlayerA waves
                19:02:05 PlayerB: gg

                """.ReplaceLineEndings("\n"),
                log);
        }

        [Fact]
        public void LocalTimeUsesGivenZoneAndFallsBackToIds()
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test", "test");
            string log = ChatLogFormatter.Format(transcript(), new ChatLogFormat { TimeZone = ChatLogTimeZone.Local }, localZone: zone);

            Assert.Contains("Started: 2026-11-23 20:00:00 UTC+02:00", log);
            Assert.Contains($"21:02:05 {TestIds.PLAYER_B}: gg", log);
        }

        [Fact]
        public void ResultsAreOptionalAndShowTeamTotals()
        {
            var t = transcript();
            var match = new MatchInProgress(7, t0, MatchType.TeamVersus,
                ImmutableDictionary<int, MatchTeam>.Empty.Add(TestIds.PLAYER_A, MatchTeam.Red).Add(TestIds.PLAYER_B, MatchTeam.Blue),
                ImmutableDictionary<int, byte>.Empty);
            var scores = new[]
            {
                new MultiplayerScore { UserId = TestIds.PLAYER_B, TotalScore = 800_000, Accuracy = 0.95, MaxCombo = 400, Passed = true, Mods = [new MultiplayerScoreMod { Acronym = "HD" }] },
                new MultiplayerScore { UserId = TestIds.PLAYER_A, TotalScore = 1_000_000, Accuracy = 0.985, MaxCombo = 500, Passed = true, User = new ApiUser { Id = TestIds.PLAYER_A, Username = "PlayerA" } },
            };
            t.AddResult(new TranscriptResult(t0.AddSeconds(600), 129891, new MatchResult(1000, 7, match, scores, 2, true)));

            string with = ChatLogFormatter.Format(t, new ChatLogFormat { IncludeResults = true });
            string without = ChatLogFormatter.Format(t, new ChatLogFormat { IncludeResults = false });

            Assert.Contains(
                """
                18:10:00 -- result: playlist item 7, beatmap 129891
                18:10:00 --   1. PlayerA (red) 1,000,000 98.50% x500
                18:10:00 --   2. 101 (blue) 800,000 95.00% x400 +HD
                18:10:00 --   totals: red 1,000,000 | blue 800,000
                19:02:05
                """.ReplaceLineEndings("\n"),
                with);
            Assert.DoesNotContain("result", without);
        }

        [Fact]
        public void SeedMergesHistoryWithoutDuplicates()
        {
            var t = transcript();
            t.Seed([msg(1, 5, "hello"), msg(0, 1, "earlier")], []);

            Assert.Equal(["earlier", "hello", "waves", "gg"], t.Messages.Select(m => m.Content));
        }
    }
}
