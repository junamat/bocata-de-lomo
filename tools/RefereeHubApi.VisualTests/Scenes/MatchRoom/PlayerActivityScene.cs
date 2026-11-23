using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.MatchRoom;

public sealed class PlayerActivityScene : AppScene
{
    public override string Description => "Hub events arriving one by one: ready-ups, a player leaving, a sub joining, team swaps, rolls and mod changes.";

    private int players => Room.Teams.Sum(t => t.Players.Length);

    [SetUpSteps]
    public void ShowRoom()
    {
        AddStep("show room", () => Main.ShowRoom(Room));
        AddStep("switch to activity tab", () => Room.SelectActivityCommand.Execute(null));
    }

    [Test]
    public void TestReadyUp()
    {
        int ready = 0;
        AddStep("Tsukuyo readies up", async () =>
        {
            ready = Room.ReadyCount;
            await SendAsync("UserStatusChanged", new UserStatusChangedEvent { RoomId = Fixtures.ROOM_ID, UserId = 102, Status = MatchUserStatus.Ready });
        });
        AddUntilStep("ready count goes up", () => Room.ReadyCount == ready + 1);
        AddToggleStep("Orrery ready", v => _ = SendAsync("UserStatusChanged", new UserStatusChangedEvent
        {
            RoomId = Fixtures.ROOM_ID, UserId = 103, Status = v ? MatchUserStatus.Ready : MatchUserStatus.Idle,
        }));
    }

    [Test]
    public void TestSubstitution()
    {
        int before = 0;
        AddStep("Haruno leaves", async () =>
        {
            before = players;
            await SendAsync("UserLeft", new UserLeftEvent { RoomId = Fixtures.ROOM_ID, UserId = 105 });
        });
        AddUntilStep("one fewer player", () => players == before - 1);

        AddStep("velvetfox joins", async () =>
        {
            before = players;
            await SendAsync("UserJoined", new UserJoinedEvent { RoomId = Fixtures.ROOM_ID, UserId = Fixtures.Substitute.Id });
        });
        AddUntilStep("velvetfox listed", () => Room.Room.State.Players.ContainsKey(Fixtures.Substitute.Id) && players == before + 1);

        AddStep("velvetfox goes blue", () => SendAsync("UserTeamChanged", new UserTeamChangedEvent { RoomId = Fixtures.ROOM_ID, UserId = Fixtures.Substitute.Id, Team = MatchTeam.Blue }));
        AddUntilStep("blue has three again", () => Room.Teams.Count == 2 && Room.Teams[1].Players.Length == 3);
    }

    [Test]
    public void TestRolls()
    {
        int activity = 0;
        AddStep("note activity", () => activity = Room.Activity.Count);
        AddRepeatStep("someone rolls", i => _ = SendAsync("RollCompleted", new RollCompletedEvent
        {
            RoomId = Fixtures.ROOM_ID, UserId = Fixtures.Players[i % Fixtures.Players.Length].Id, Max = 100, Result = (uint)(17 + 29 * i % 83),
        }), 4);
        AddUntilStep("four rolls logged", () => Room.Activity.Count >= activity + 4);
    }

    [TestCase("HD,HR")]
    [TestCase("DT")]
    [TestCase("")]
    public void TestModsChange(string list)
    {
        string[] mods = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
        AddStep($"Orrery picks {(mods.Length == 0 ? "no mods" : string.Concat(mods))}", () => SendAsync("UserModsChanged", new UserModsChangedEvent
        {
            RoomId = Fixtures.ROOM_ID, UserId = 103, UserMods = mods.Select(m => new Mod(m)).ToArray(),
        }));
        AddUntilStep("mods updated", () => Room.Room.State.Players[103].Mods.Select(m => m.Acronym).SequenceEqual(mods));
    }
}
