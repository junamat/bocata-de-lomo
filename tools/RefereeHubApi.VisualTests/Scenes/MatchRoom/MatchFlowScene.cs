using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.MatchRoom;

public sealed class MatchFlowScene : AppScene
{
    private const long playlist_item = 5;

    public override string Description => "Start countdown, the map playing, then completion fetching scores and opening results.";

    public MatchFlowScene()
    {
        Api.OnJson("GET", $"/api/v2/rooms/{Fixtures.ROOM_ID}/playlist/{playlist_item}/scores", _ => Fixtures.ScoresJson(
            (103, 830112, 0.9864, "S", true), (100, 812440, 0.9821, "S", true), (101, 744019, 0.9688, "A", true),
            (104, 721905, 0.9702, "A", true), (105, 674661, 0.9678, "A", true), (102, 628447, 0.9127, "F", false)));
    }

    [SetUpSteps]
    public void ShowRoom() => AddStep("show room", () => Main.ShowRoom(Room));

    [Test]
    public void TestCountdown()
    {
        AddStep("start 30s countdown", () => SendAsync("CountdownStarted", new CountdownStartedEvent
        {
            RoomId = Fixtures.ROOM_ID, CountdownId = 1, Seconds = 30, Type = CountdownType.MatchStart,
        }));
        AddUntilStep("countdown shows", () => Room.Counting);
        AddWaitStep("let it tick", 10);
        AddStep("stop countdown", () => SendAsync("CountdownStopped", new CountdownStoppedEvent { RoomId = Fixtures.ROOM_ID, CountdownId = 1, Type = CountdownType.MatchStart }));
        AddUntilStep("countdown gone", () => !Room.Counting);
    }

    [Test]
    public void TestPlayAndAbort()
    {
        AddStep("match starts", startMatch);
        AddUntilStep("room shows in progress", () => Room.InProgress);
        AddStep("match aborted", () => SendAsync("MatchAborted", new MatchAbortedEvent { RoomId = Fixtures.ROOM_ID, PlaylistItemId = playlist_item }));
        AddUntilStep("back to waiting", () => !Room.InProgress);
    }

    [Test]
    public void TestPlayToResults()
    {
        AddStep("match starts", startMatch);
        AddUntilStep("room shows in progress", () => Room.InProgress);
        AddStep("match completes", () => SendAsync("MatchCompleted", new MatchCompletedEvent { RoomId = Fixtures.ROOM_ID, PlaylistItemId = playlist_item }));
        AddUntilStep("results open", () => Main.Page is ResultsViewModel);
        AddAssert("Orrery tops the table", () => ((ResultsViewModel)Main.Page!).Rows[0].Name == "Orrery");
    }

    private Task startMatch() => SendAsync("MatchStarted", new MatchStartedEvent
    {
        RoomId = Fixtures.ROOM_ID,
        PlaylistItemId = playlist_item,
        MatchType = MatchType.TeamVersus,
        Teams = Fixtures.Players.ToDictionary(p => p.Id, p => p.Team),
        Slots = Fixtures.Players.Select((p, i) => (p.Id, (byte)i)).ToDictionary(x => x.Id, x => x.Item2),
    });
}
