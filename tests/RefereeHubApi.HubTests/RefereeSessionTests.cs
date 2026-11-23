using Microsoft.AspNetCore.SignalR;
using RefereeHubApi.Commands;
using RefereeHubApi.Hub;
using RefereeHubApi.Journal;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Session;
using RefereeHubApi.Settings;
using RefereeHubApi.State;
using RefereeHubApi.Tests.Infrastructure;
using RefereeHubApi.Users;

namespace RefereeHubApi.Tests
{
    public class RefereeSessionTests : IAsyncLifetime
    {
        private FakeHubServer server = null!;
        private readonly StubHttpHandler api = new StubHttpHandler();

        public async Task InitializeAsync()
        {
            server = await FakeHubServer.StartAsync();
            api.OnJson("GET", "/api/v2/me", _ => ApiFixtures.Me);
        }

        public async Task DisposeAsync() => await server.DisposeAsync();

        private Task<RefereeSession> start(Action<RefereeSessionOptions>? configure = null)
        {
            var options = new RefereeSessionOptions
            {
                Hub = server.ConnectionOptions(),
                ApiBaseUrl = new Uri("http://api.test/api/v2/"),
                EnableChat = false,
                ScoreFetchDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
            };
            configure?.Invoke(options);
            return RefereeSession.StartAsync(new FakeCredentials(), options, new HttpClient(api));
        }

        [Fact]
        public async Task StartResolvesSelfAndRejoinsListedRooms()
        {
            server.State.ListedRooms.AddRange([7, 8]);

            await using var session = await start();

            Assert.Equal(TestIds.SELF, session.Self.Id);
            Assert.Equal([7L, 8L], session.Rooms.Select(r => r.RoomId).Order());
            Assert.Equal(["ListRooms", "JoinRoom", "JoinRoom"], server.State.MethodNames);
        }

        [Fact]
        public async Task MakeRoomChecksLimit()
        {
            server.State.ListedRooms.AddRange([1, 2, 3, 4]);
            await using var session = await start(o => o.JoinListedRoomsOnStart = false);

            var ex = await Assert.ThrowsAsync<RoomLimitReachedException>(() => session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75, RoomName = "x" }));
            Assert.Equal(4, ex.OpenRoomIds.Count);
            Assert.DoesNotContain("MakeRoom", server.State.MethodNames);

            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75, RoomName = "x" }, ignoreRoomLimit: true);
            Assert.Equal("x", room.State.Name);
        }

        [Fact]
        public async Task RoomMethodsReachHub()
        {
            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75, RoomName = "TEST: (A) vs (B)" });

            await room.InviteAsync(TestIds.PLAYER_A);
            await room.SetMatchTypeAsync(MatchType.TeamVersus);
            await room.SetMapAsync(129891);
            await room.SetModsAsync([new Mod("HD")], freestyle: true);
            await room.SetTeamAsync(TestIds.PLAYER_A, MatchTeam.Blue);
            await room.SetLockedAsync(true);
            await room.StartAsync(10);
            await room.StopCountdownAsync();
            await room.StartAsync();
            await room.AbortAsync();
            await room.RollAsync();
            await room.RollAsync(50);
            await room.KickAsync(TestIds.PLAYER_A);
            await room.BanAsync(TestIds.PLAYER_A);
            await room.AddRefereeAsync(TestIds.OTHER_REF);
            await room.RemoveRefereeAsync(TestIds.OTHER_REF);
            await room.AddPlaylistItemAsync(new AddPlaylistItemRequest { BeatmapId = 1 });
            await room.EditPlaylistItemAsync(new EditPlaylistItemRequest { PlaylistItemId = 1, BeatmapId = 2 });
            await room.RemovePlaylistItemAsync(1);
            await room.SetSizeAsync(4);
            await room.CloseAsync();

            var calls = server.State.Calls.ToArray();
            Assert.Contains(calls, c => c.Method == "InvitePlayer" && (long)c.Args[0]! == room.RoomId && (int)c.Args[1]! == TestIds.PLAYER_A);

            var mods = (EditCurrentPlaylistItemRequest)calls.Last(c => c.Method == "EditCurrentPlaylistItem").Args[1]!;
            Assert.Equal("HD", Assert.Single(mods.RequiredMods!).Acronym);
            Assert.True(mods.Freestyle);
            Assert.Empty(mods.AllowedMods!);

            var move = (MoveUserRequest)calls.Single(c => c.Method == "MoveUser").Args[1]!;
            Assert.Equal(MatchTeam.Blue, move.Team);

            var starts = calls.Where(c => c.Method == "StartMatch").Select(c => ((StartGameplayRequest)c.Args[1]!).Countdown).ToArray();
            Assert.Equal([10, null], starts);

            var rolls = calls.Where(c => c.Method == "Roll").Select(c => ((RollRequest?)c.Args[1])?.Max).ToArray();
            Assert.Equal([null, 50u], rolls);

            Assert.Contains("CloseRoom", server.State.MethodNames);
        }

        [Fact]
        public async Task RoomActionsAcceptUsernames()
        {
            api.OnJson("GET", "/api/v2/users/@", _ => new { id = TestIds.PLAYER_B, username = "Player B" });

            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });

            await room.InviteAsync(UserRef.Parse("Player B"));
            await room.SetTeamAsync(UserRef.Parse("player_b"), MatchTeam.Red);
            await Assert.ThrowsAsync<UserNotFoundException>(async () =>
            {
                api.On("GET", "/api/v2/users/@nobody", _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                await room.KickAsync(UserRef.Parse("nobody"));
            });

            var calls = server.State.Calls.ToArray();
            Assert.Contains(calls, c => c.Method == "InvitePlayer" && (int)c.Args[1]! == TestIds.PLAYER_B);
            Assert.Equal(TestIds.PLAYER_B, ((MoveUserRequest)calls.Single(c => c.Method == "MoveUser").Args[1]!).UserId);
            Assert.DoesNotContain("KickPlayer", server.State.MethodNames);
            Assert.Equal(1, api.Count("GET", "/api/v2/users/@Player"));
        }

        [Fact]
        public async Task CommandsDriveTheHub()
        {
            await using var session = await start();
            var output = new List<string>();
            var ctx = new CommandContext
            {
                Session = session,
                Output = output.Add,
                Settings = new RefereeSettings { Macros = { ["setup"] = ["type tv", "lock", "invite #100", "map 129891 0"] } },
            };

            await CommandTable.Default.ExecuteAsync("make 75 0 TEST: (A) vs (B)", ctx);
            Assert.Equal("TEST: (A) vs (B)", ctx.Room!.State.Name);

            await CommandTable.Default.ExecuteAsync("macro setup", ctx);
            await CommandTable.Default.ExecuteAsync("state", ctx);

            var methods = server.State.MethodNames.SkipWhile(m => m != "MakeRoom").Skip(1).ToArray();
            Assert.Equal(["ChangeRoomSettings", "SetLockState", "InvitePlayer", "EditCurrentPlaylistItem"], methods);
            Assert.Contains(output, o => o.StartsWith($"Room {ctx.Room.RoomId} 'TEST: (A) vs (B)'"));
        }

        [Fact]
        public async Task MacroMayPrivateMessageOnlyOneUser()
        {
            api.OnJson("GET", "/api/v2/users/@", r => new { id = r.RequestUri!.AbsolutePath.EndsWith("alias") ? TestIds.PLAYER_A : TestIds.PLAYER_B, username = "x" });

            await using var session = await start();
            var ctx = new CommandContext
            {
                Session = session,
                Settings = new RefereeSettings
                {
                    Macros =
                    {
                        ["two"] = ["lock", $"pm #{TestIds.PLAYER_A} hi", $"pm #{TestIds.PLAYER_B} hi"],
                        ["same"] = ["lock", $"pm #{TestIds.PLAYER_A} hi", "pm alias hi again"],
                    },
                },
            };
            await CommandTable.Default.ExecuteAsync("make 75 0 x", ctx);

            var ex = await Assert.ThrowsAsync<CommandException>(() => CommandTable.Default.ExecuteAsync("macro two", ctx));
            Assert.Contains("only message one", ex.Message);
            Assert.DoesNotContain("SetLockState", server.State.MethodNames);

            // same user by id and by name passes validation; chat is off in these tests, so the send itself fails.
            await Assert.ThrowsAsync<InvalidOperationException>(() => CommandTable.Default.ExecuteAsync("macro same", ctx));
            Assert.Contains("SetLockState", server.State.MethodNames);
        }

        [Fact]
        public async Task JournalRecordsUntilRoomEndsAndIsOnlyHistory()
        {
            var dir = Directory.CreateTempSubdirectory("refereehub-journal-");

            try
            {
                var journal = new RoomJournal(dir.FullName);

                // left over from an earlier run: its chat is history, its snapshot must not become live state.
                await journal.SaveAsync(new RoomJournalEntry
                {
                    RoomId = 7,
                    Name = "stale name",
                    Snapshot = new RoomSnapshot { RoomId = 7, Name = "stale name" },
                    Chat = [new Api.ChatMessage { MessageId = 1, ChannelId = 555, SenderId = TestIds.PLAYER_A, Content = "before restart" }],
                });

                server.State.ListedRooms.Add(7);
                await using var session = await start(o =>
                {
                    o.Journal = journal;
                    o.JournalSaveDelay = TimeSpan.FromMilliseconds(10);
                });

                var room = session.GetRoom(7)!;
                Assert.Equal("TEST: (Red) vs (Blue)", room.State.Name);
                Assert.Equal("before restart", Assert.Single(room.Transcript.Messages).Content);

                await server.SendAsync("UserJoined", new UserJoinedEvent { RoomId = 7, UserId = TestIds.PLAYER_A });
                await Wait.Until(() => journal.LoadAsync(7).Result?.Snapshot?.Players.ContainsKey(TestIds.PLAYER_A) == true);

                var saved = (await journal.LoadAsync(7))!;
                Assert.Equal("TEST: (Red) vs (Blue)", saved.Name);
                Assert.Equal(string.Empty, saved.Snapshot!.Password);
                Assert.Equal("before restart", Assert.Single(saved.Chat).Content);

                // a second room stays in the journal after the session goes away.
                var other = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 }, ignoreRoomLimit: true);

                await server.SendAsync("UserKicked", new UserKickedEvent { RoomId = 7, KickedUserId = TestIds.SELF, KickingUserId = TestIds.OTHER_REF });
                await Wait.Until(() => journal.LoadAsync(7).Result == null);

                await session.DisposeAsync();
                Assert.Equal([other.RoomId], (await journal.ListAsync()).Select(e => e.RoomId));
                Assert.DoesNotContain("JoinRoom", server.State.MethodNames.SkipWhile(m => m != "MakeRoom"));
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        }

        [Fact]
        public async Task HubErrorsAreMapped()
        {
            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });
            server.State.Errors["InvitePlayer"] = "Error 19: The specified user is not connected to multiplayer.";

            var ex = await Assert.ThrowsAsync<RefereeHubException>(() => room.InviteAsync(123));

            Assert.Equal(RefereeHubErrorCode.UserNotConnected, ex.Code);
            Assert.Equal("InvitePlayer", ex.MethodName);
        }

        [Fact]
        public async Task EventsUpdateStateAndSelfKickRemovesRoom()
        {
            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });
            long id = room.RoomId;

            await server.SendAsync("UserJoined", new UserJoinedEvent { RoomId = id, UserId = TestIds.PLAYER_A });
            await server.SendAsync("UserStatusChanged", new UserStatusChangedEvent { RoomId = id, UserId = TestIds.PLAYER_A, Status = MatchUserStatus.Ready });

            await Wait.For(room.StateChanged, s => s.Players.TryGetValue(TestIds.PLAYER_A, out var p) && p.Status == MatchUserStatus.Ready);

            var removed = Wait.For(session.RoomRemoved);
            await server.SendAsync("UserKicked", new UserKickedEvent { RoomId = id, KickedUserId = TestIds.SELF, KickingUserId = TestIds.OTHER_REF });

            Assert.Same(room, await removed);
            Assert.Equal(RoomEndReason.ClosedOrKicked, room.State.Ended);
            Assert.Empty(session.Rooms);
        }

        [Fact]
        public async Task EventsRacingMakeRoomResultAreKept()
        {
            server.State.BeforeRoomReturn = (clients, id) => clients.Caller.SendAsync("UserJoined", new UserJoinedEvent { RoomId = id, UserId = TestIds.PLAYER_A });

            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });

            await Wait.Until(() => room.State.Players.ContainsKey(TestIds.PLAYER_A));
        }

        [Fact]
        public async Task CompletedMatchFetchesScores()
        {
            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });
            long id = room.RoomId;

            api.OnJson("GET", $"/api/v2/rooms/{id}/playlist/1/scores", _ => new
            {
                scores = new object[]
                {
                    new { id = 1, user_id = TestIds.PLAYER_A, total_score = 900000, accuracy = 0.98, max_combo = 500, mods = Array.Empty<object>(), passed = true, rank = "S" },
                    new { id = 2, user_id = TestIds.PLAYER_B, total_score = 800000, accuracy = 0.95, max_combo = 400, mods = new[] { new { acronym = "HD" } }, passed = true, rank = "A" },
                },
                total = 2,
                cursor_string = (string?)null,
            });

            await server.SendAsync("MatchStarted", new MatchStartedEvent
            {
                RoomId = id,
                PlaylistItemId = 1,
                MatchType = MatchType.TeamVersus,
                Teams = new() { [TestIds.PLAYER_A] = MatchTeam.Red, [TestIds.PLAYER_B] = MatchTeam.Blue },
                Slots = new() { [TestIds.PLAYER_A] = 0, [TestIds.PLAYER_B] = 1 },
            });
            await Wait.For(room.StateChanged, s => s.IsMatchInProgress);

            var result = Wait.For(room.Results);
            await server.SendAsync("MatchCompleted", new MatchCompletedEvent { RoomId = id, PlaylistItemId = 1 });

            var r = await result;
            Assert.True(r.Complete);
            Assert.Equal(2, r.ExpectedScoreCount);
            Assert.Equal(MatchTeam.Blue, r.Match!.Teams[TestIds.PLAYER_B]);
            Assert.Equal(900000, r.Scores.Single(s => s.UserId == TestIds.PLAYER_A).TotalScore);

            var logged = Assert.Single(room.Transcript.Results);
            Assert.Equal(75, logged.BeatmapId);
            Assert.Contains("-- result: playlist item 1, beatmap 75", room.ExportLog());
        }

        [Fact]
        public async Task ServerShutdownWarnsAndInvitationsSurface()
        {
            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });

            var warning = Wait.For(session.ServerShuttingDown);
            await server.SendAsync("CountdownStarted", new CountdownStartedEvent { RoomId = room.RoomId, CountdownId = 1, Seconds = 21600, Type = CountdownType.ServerShuttingDown });
            Assert.Equal(TimeSpan.FromHours(6), (await warning).TimeRemaining);

            var invitation = Wait.For(session.Invitations);
            await server.SendAsync("RefereeInvited", new RefereeInvitedEvent { RoomId = 4242 });
            Assert.Equal(4242, (await invitation).RoomId);
        }

        [Fact]
        public async Task ReconnectCanCloseRoomsThenRejoinsRest()
        {
            await using var session = await start();
            var room = await session.MakeRoomAsync(new MakeRoomRequest { BeatmapId = 75 });

            await session.ReconnectAsync();
            Assert.Equal(2, server.State.MethodNames.Count(m => m == "JoinRoom") + server.State.MethodNames.Count(m => m == "MakeRoom"));
            Assert.Same(room, session.GetRoom(room.RoomId));

            await session.ReconnectAsync(closeRoomsFirst: true);
            Assert.Contains("CloseRoom", server.State.MethodNames);
            Assert.Empty(session.Rooms);
        }
    }
}
