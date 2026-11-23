using RefereeHubApi.Session;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.RoomList;

public sealed class ConnectFailedScene : AppScene
{
    public override string Description => "The hub can't be reached at sign-in: offline badge, error toast and retry.";

    protected override long[] ListedRooms => [];

    protected override Task<RefereeSession> StartSessionAsync(RefereeSessionOptions options, CancellationToken ct)
        => Task.FromException<RefereeSession>(new HttpRequestException("Connection refused (hub.test:443)"));

    [Test]
    public void TestHubUnreachable()
    {
        AddUntilStep("connect error shown", () => Main.RoomsPage!.ConnectError != null);
        AddAssert("hub badge offline", () => Main.HubText == "Offline");
        AddAssert("toast raised", () => Main.HasToast);
    }
}
