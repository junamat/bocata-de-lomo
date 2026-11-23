using System.Collections.Immutable;
using RefereeHubApi.Api;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Journal;
using RefereeHubApi.Logs;
using RefereeHubApi.Session;
using RefereeHubApi.State;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public sealed class RoomJournalTests : IDisposable
    {
        private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("refereehub-journal-");

        public void Dispose() => dir.Delete(recursive: true);

        private static RoomJournalEntry entry(long roomId, DateTimeOffset savedAt)
        {
            var snapshot = RoomStateReducer.FromJoin(RoomFixtures.DefaultRoom(roomId)) with
            {
                Players = ImmutableDictionary<int, PlayerState>.Empty.Add(TestIds.PLAYER_A, new PlayerState
                {
                    UserId = TestIds.PLAYER_A,
                    Status = MatchUserStatus.Ready,
                    Team = MatchTeam.Blue,
                    Slot = 1,
                    Mods = [new Mod("DT", new Dictionary<string, object> { ["speed_change"] = 1.2 })],
                }),
                Ended = null,
            };

            var match = new MatchInProgress(1, savedAt, MatchType.TeamVersus, ImmutableDictionary<int, MatchTeam>.Empty.Add(TestIds.PLAYER_A, MatchTeam.Blue), ImmutableDictionary<int, byte>.Empty);

            return new RoomJournalEntry
            {
                RoomId = roomId,
                Name = snapshot.Name,
                StartedAt = savedAt.AddMinutes(-30),
                SavedAt = savedAt,
                Snapshot = snapshot,
                Chat = [new ChatMessage { MessageId = 1, SenderId = TestIds.PLAYER_A, ChannelId = 555, Content = "hi", Timestamp = savedAt }],
                Results = [new TranscriptResult(savedAt, 75, new MatchResult(roomId, 1, match, [new MultiplayerScore { UserId = TestIds.PLAYER_A, TotalScore = 123 }], 1, true))],
            };
        }

        [Fact]
        public async Task RoundTripsWithoutPassword()
        {
            var journal = new RoomJournal(dir.FullName);
            var saved = entry(1000, DateTimeOffset.Parse("2026-11-23T18:00:00Z"));
            Assert.Equal("pw", saved.Snapshot!.Password);

            await journal.SaveAsync(saved);
            var loaded = (await journal.LoadAsync(1000))!;

            Assert.DoesNotContain("\"pw\"", await File.ReadAllTextAsync(Path.Combine(dir.FullName, "room-1000.json")));
            Assert.Equal(string.Empty, loaded.Snapshot!.Password);
            Assert.Equal(saved.Snapshot.Name, loaded.Snapshot.Name);
            var player = loaded.Snapshot.Players[TestIds.PLAYER_A];
            Assert.Equal(MatchTeam.Blue, player.Team);
            Assert.Equal(1, player.Slot);
            Assert.Equal("DT", Assert.Single(player.Mods).Acronym);
            Assert.Equal("hi", Assert.Single(loaded.Chat).Content);
            var result = Assert.Single(loaded.Results);
            Assert.Equal(75, result.BeatmapId);
            Assert.Equal(MatchTeam.Blue, result.Result.Match!.Teams[TestIds.PLAYER_A]);
            Assert.Equal(123, Assert.Single(result.Result.Scores).TotalScore);
        }

        [Fact]
        public async Task ListsNewestFirstSkippingBrokenFilesAndDeletes()
        {
            var journal = new RoomJournal(dir.FullName);
            var t = DateTimeOffset.Parse("2026-11-23T18:00:00Z");
            await journal.SaveAsync(entry(1, t));
            await journal.SaveAsync(entry(2, t.AddMinutes(5)));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "room-3.json"), "{broken");

            Assert.Equal([2L, 1L], (await journal.ListAsync()).Select(e => e.RoomId));

            await journal.DeleteAsync(2);
            await journal.DeleteAsync(99);
            Assert.Equal([1L], (await journal.ListAsync()).Select(e => e.RoomId));
            Assert.DoesNotContain(Directory.GetFiles(dir.FullName), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MissingDirectoryListsNothing()
            => Assert.Empty(await new RoomJournal(Path.Combine(dir.FullName, "nope")).ListAsync());
    }
}
