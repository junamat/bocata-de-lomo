using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.RoomList;

public sealed class NoRoomsScene : AppScene
{
    public override string Description => "Connected, nothing open yet: the empty home page.";

    protected override long[] ListedRooms => [];

    [Test]
    public void TestEmpty()
    {
        AddUntilStep("hub connected", () => Main.HubText == "Connected");
        AddAssert("no rooms", () => Main.RoomsPage!.Rooms.Count == 0 && Main.RoomTabs.Count == 0);
    }
}
