using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.Toolbar;

public sealed class ServerRestartScene : AppScene
{
    public override string Description => "The hub announces a restart: red banner with the countdown and reconnect buttons.";

    [Test]
    public void TestShutdownWarning()
    {
        AddStep("show room", () => Main.ShowRoom(Room));
        AddStep("hub warns of shutdown in 5 minutes", () => SendAsync("CountdownStarted", new CountdownStartedEvent
        {
            RoomId = Fixtures.ROOM_ID, CountdownId = 9, Seconds = 300, Type = CountdownType.ServerShuttingDown,
        }));
        AddUntilStep("banner visible", () => Main.ShutdownVisible);
        AddAssert("clock counting down", () => Main.ShutdownClock.StartsWith("4:") || Main.ShutdownClock.StartsWith("5:"));
    }
}
