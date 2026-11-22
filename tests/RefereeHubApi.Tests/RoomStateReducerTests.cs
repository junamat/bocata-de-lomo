using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.State;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class RoomStateReducerTests
    {
        private const long room_id = 1000;
        private static readonly DateTimeOffset now = new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);

        private static RoomSnapshot initial()
        {
            var response = RoomFixtures.DefaultRoom(room_id);
            response.Players = [new Player { UserId = TestIds.PLAYER_A, Status = MatchUserStatus.Idle }];
            response.State.Slots = [TestIds.PLAYER_A, null];
            response.Playlist =
            [
                new PlaylistItem { PlaylistItemId = 1, BeatmapId = 75, Order = 0, WasPlayed = true },
                new PlaylistItem { PlaylistItemId = 2, BeatmapId = 76, Order = 1 },
            ];
            return RoomStateReducer.FromJoin(response);
        }

        private static RoomSnapshot apply(RoomSnapshot s, IRoomEvent e) => RoomStateReducer.Apply(s, e, TestIds.SELF, now);

        [Fact]
        public void JoinBuildsSnapshot()
        {
            var s = initial();

            Assert.Equal(2, s.CurrentPlaylistItemId); // first unplayed
            Assert.Equal(0, s.Players[TestIds.PLAYER_A].Slot);
            Assert.Contains(TestIds.SELF, s.Referees);
            Assert.Contains(TestIds.SELF, s.JoinedReferees);
            Assert.Null(s.MaxParticipants); // 0 = no limit
        }

        [Fact]
        public void UserJoinedClassifiesRefereesAndPlayers()
        {
            var s = apply(initial(), new RefereeAddedEvent { RoomId = room_id, UserId = TestIds.OTHER_REF });
            s = apply(s, new UserJoinedEvent { RoomId = room_id, UserId = TestIds.OTHER_REF });
            s = apply(s, new UserJoinedEvent { RoomId = room_id, UserId = TestIds.PLAYER_B });

            Assert.Contains(TestIds.OTHER_REF, s.JoinedReferees);
            Assert.False(s.Players.ContainsKey(TestIds.OTHER_REF));
            Assert.Equal(MatchUserStatus.Idle, s.Players[TestIds.PLAYER_B].Status);

            s = apply(s, new UserLeftEvent { RoomId = room_id, UserId = TestIds.PLAYER_B });
            Assert.False(s.Players.ContainsKey(TestIds.PLAYER_B));
        }

        [Fact]
        public void PlayerUpdatesApply()
        {
            var s = initial();
            s = apply(s, new UserStatusChangedEvent { RoomId = room_id, UserId = TestIds.PLAYER_A, Status = MatchUserStatus.Ready });
            s = apply(s, new UserTeamChangedEvent { RoomId = room_id, UserId = TestIds.PLAYER_A, Team = MatchTeam.Blue });
            s = apply(s, new UserModsChangedEvent { RoomId = room_id, UserId = TestIds.PLAYER_A, UserMods = [new Mod("HD")] });
            s = apply(s, new UserStyleChangedEvent { RoomId = room_id, UserId = TestIds.PLAYER_A, BeatmapId = 80, RulesetId = 1 });

            var p = s.Players[TestIds.PLAYER_A];
            Assert.Equal(MatchUserStatus.Ready, p.Status);
            Assert.Equal(MatchTeam.Blue, p.Team);
            Assert.Equal("HD", Assert.Single(p.Mods).Acronym);
            Assert.Equal(80, p.StyleBeatmapId);
        }

        [Fact]
        public void SettingsAndMatchStateApply()
        {
            var s = apply(initial(), new RoomSettingsChangedEvent
            {
                RoomId = room_id, Name = "new", Password = "", Type = MatchType.TeamVersus, QueueMode = QueueMode.AllPlayers, PlaylistItemId = 1, MaxParticipants = 8,
            });

            Assert.Equal("new", s.Name);
            Assert.Equal(MatchType.TeamVersus, s.MatchType);
            Assert.Equal(1, s.CurrentPlaylistItemId);
            Assert.Equal((byte)8, s.MaxParticipants);

            s = apply(s, new MatchStateChangedEvent { RoomId = room_id, State = new MatchState { Type = MatchType.TeamVersus, Locked = true, Slots = [null, TestIds.PLAYER_A] } });
            Assert.True(s.Locked);
            Assert.Equal(1, s.Players[TestIds.PLAYER_A].Slot);
        }

        [Fact]
        public void PlaylistEventsKeepOrder()
        {
            var s = apply(initial(), new PlaylistItemAddedEvent { RoomId = room_id, PlaylistItem = new PlaylistItem { PlaylistItemId = 3, Order = 0 } });
            Assert.Equal([1L, 3L, 2L], s.Playlist.Select(p => p.PlaylistItemId));

            s = apply(s, new PlaylistItemChangedEvent { RoomId = room_id, PlaylistItem = new PlaylistItem { PlaylistItemId = 3, BeatmapId = 99, Order = 5 } });
            Assert.Equal(3, s.Playlist.Last().PlaylistItemId);
            Assert.Equal(99, s.Playlist.Last().BeatmapId);

            s = apply(s, new PlaylistItemRemovedEvent { RoomId = room_id, PlaylistItemId = 3 });
            Assert.Equal([1L, 2L], s.Playlist.Select(p => p.PlaylistItemId));
        }

        [Fact]
        public void CountdownAndMatchLifecycle()
        {
            var s = apply(initial(), new CountdownStartedEvent { RoomId = room_id, CountdownId = 7, Seconds = 10, Type = CountdownType.MatchStart });
            Assert.Equal(now.AddSeconds(10), s.MatchStartCountdown!.EndsAt);

            s = apply(s, new CountdownStoppedEvent { RoomId = room_id, CountdownId = 7, Type = CountdownType.MatchStart });
            Assert.Empty(s.Countdowns);

            s = apply(s, new MatchStartedEvent { RoomId = room_id, PlaylistItemId = 2, MatchType = MatchType.TeamVersus, Teams = new() { [TestIds.PLAYER_A] = MatchTeam.Red } });
            Assert.True(s.IsMatchInProgress);
            Assert.Equal(MatchTeam.Red, s.Match!.Teams[TestIds.PLAYER_A]);

            s = apply(s, new MatchCompletedEvent { RoomId = room_id, PlaylistItemId = 2 });
            Assert.False(s.IsMatchInProgress);
            Assert.Equal(2, s.LastCompletedPlaylistItemId);
        }

        [Fact]
        public void SelfKickEndsRoom()
        {
            var s = apply(initial(), new UserKickedEvent { RoomId = room_id, KickedUserId = TestIds.PLAYER_A, KickingUserId = TestIds.SELF });
            Assert.Null(s.Ended);

            s = apply(s, new UserKickedEvent { RoomId = room_id, KickedUserId = TestIds.SELF, KickingUserId = TestIds.OTHER_REF });
            Assert.Equal(RoomEndReason.ClosedOrKicked, s.Ended);
        }

        [Fact]
        public void SelfRefereeRemovalEndsRoom()
        {
            var s = apply(initial(), new RefereeRemovedEvent { RoomId = room_id, UserId = TestIds.SELF });
            Assert.Equal(RoomEndReason.RefereeRemoved, s.Ended);
        }

        [Fact]
        public void BanRemovesAndRecords()
        {
            var s = apply(initial(), new UserBannedEvent { RoomId = room_id, BannedUserId = TestIds.PLAYER_A, BanningUserId = TestIds.SELF });
            Assert.False(s.Players.ContainsKey(TestIds.PLAYER_A));
            Assert.Contains((long)TestIds.PLAYER_A, s.BannedUsers);
        }

        [Fact]
        public void EventForOtherRoomRejected()
            => Assert.Throws<ArgumentException>(() => apply(initial(), new UserJoinedEvent { RoomId = 1, UserId = 1 }));
    }
}
