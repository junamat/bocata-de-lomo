using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.Toolbar;

public sealed class ErrorToastScene : AppScene
{
    public override string Description => "An error toast over the room, then dismissed.";

    [Test]
    public void TestToast()
    {
        AddStep("show room", () => Main.ShowRoom(Room));
        AddStep("report an error", () => Services.ReportError("Hub error 4 (NotHost): Only the host can change the beatmap."));
        AddUntilStep("toast shows", () => Main.HasToast);
        AddStep("dismiss", () => Main.DismissToastCommand.Execute(null));
        AddAssert("toast gone", () => !Main.HasToast);
    }

    [Test]
    public void TestLongMessage()
    {
        AddStep("show room", () => Main.ShowRoom(Room));
        AddStep("report a long error", () => Services.ReportError(
            "Network error: The SSL connection could not be established, see inner exception. Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'."));
        AddUntilStep("toast shows", () => Main.HasToast);
    }
}
