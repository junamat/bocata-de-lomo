using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.SettingsPage;

public sealed class MacrosScene : AppScene
{
    public override string Description => "Settings with a macro that has a self-reference and an unknown command flagged.";

    protected override long[] ListedRooms => [];

    [SetUpSteps]
    public void OpenSettings() => AddStep("open settings", () => Main.ShowSettings());

    [TestCase("broken", 2)]
    [TestCase("intro", 0)]
    public void TestMacro(string name, int bad)
    {
        AddStep($"select {name}", () =>
        {
            var s = Main.SettingsPage!;
            s.SelectedMacro = s.Macros.First(m => m.Name == name);
        });
        AddAssert($"{bad} bad lines", () => Main.SettingsPage!.MacroLines.Count(l => l.Bad) == bad);
    }
}
