using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.Setup;

public sealed class SetupScreenScene : AppScene
{
    public override string Description => "First run: OAuth app details and sign-in, before any session exists.";

    protected override bool ConnectOnStart => false;

    protected override long[] ListedRooms => [];

    [Test]
    public void TestFirstRun()
    {
        AddStep("show setup", () => Main.Page = new SetupViewModel(Main));
        AddAssert("toolbar hidden while signed out", () => !Main.IsSignedIn);
    }
}
