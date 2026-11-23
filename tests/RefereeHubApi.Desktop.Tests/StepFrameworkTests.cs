using Avalonia.Controls;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.Desktop.Tests;

// the scene/step machinery itself, on a scene that records what ran.
// VisualScene timing settings are static, so these classes take turns.
[Collection("visual scenes")]
public sealed class StepFrameworkTests
{
    public StepFrameworkTests()
    {
        VisualScene.StepDelay = TimeSpan.Zero;
        VisualScene.Pump = () => Task.Delay(1);
    }

    private sealed class RecordingScene : VisualScene
    {
        public List<string> Log { get; } = new List<string>();

        public bool FailSecond { get; set; }

        public override Task<Control> CreateAsync() => Task.FromResult<Control>(new Panel());

        [SetUpSteps]
        public void Prepare() => AddStep("prepare", () => Log.Add("setup"));

        [Test]
        public void TestFirst()
        {
            AddStep("a", () => Log.Add("a"));
            AddRepeatStep("r", i => Log.Add($"r{i}"), 3);
            AddToggleStep("t", v => Log.Add($"t{v}"));
            AddSliderStep("s", 0, 10, 5, v => Log.Add($"s{v}"));
            AddWaitStep("w", 2);
        }

        [TestCase(1)]
        [TestCase("x")]
        public void TestCase(object arg)
        {
            AddAssert("ok", () => !FailSecond);
            AddStep("after", () => Log.Add($"after{arg}"));
        }
    }

    [Fact]
    public void TestsBecomeLabelsWithSetUpUnderEach()
    {
        var scene = new RecordingScene();
        scene.BuildSteps();

        var labels = scene.Steps.OfType<LabelStep>().Select(l => l.Name).ToArray();
        Assert.Equal(["First", "Case(1)", "Case(\"x\")"], labels);

        // label, then the set-up step, then the test's own steps.
        Assert.IsType<LabelStep>(scene.Steps[0]);
        Assert.True(((SceneStep)scene.Steps[1]).IsSetUp);
        Assert.Equal(3, scene.Steps.OfType<SceneStep>().Count(s => s.IsSetUp));
    }

    [Fact]
    public async Task RunAllRepeatsTogglesAndSkipsSliders()
    {
        var scene = new RecordingScene();
        scene.BuildSteps();

        Assert.True(await scene.RunAllAsync());
        Assert.Equal(["setup", "a", "r0", "r1", "r2", "tTrue", "tFalse", "setup", "after1", "setup", "afterx"], scene.Log);

        var repeat = scene.Steps.OfType<SceneStep>().Single(s => s.Name == "r");
        Assert.Equal("r 3/3", repeat.Text);
        Assert.Equal("w 2/2", scene.Steps.OfType<SceneStep>().Single(s => s.Name == "w").Text);
    }

    [Fact]
    public async Task FailureStopsTheRun()
    {
        var scene = new RecordingScene { FailSecond = true };
        scene.BuildSteps();

        Assert.False(await scene.RunAllAsync());
        var failed = scene.Steps.OfType<SceneStep>().Single(s => s.State == StepState.Failed);
        Assert.Equal("ok", failed.Name);
        Assert.Contains("Assertion failed", failed.Error);
        Assert.All(scene.Steps.OfType<SceneStep>().Where(s => s.Name == "after"), s => Assert.Equal(StepState.Pending, s.State));
    }

    [Fact]
    public async Task RunTestResetsEverythingAndRunsOnlyThatTest()
    {
        var scene = new RecordingScene();
        scene.BuildSteps();
        await scene.RunAllAsync();
        scene.Log.Clear();

        var second = scene.Steps.OfType<LabelStep>().ElementAt(1);
        Assert.True(await scene.RunTestAsync(second));

        Assert.Equal(["setup", "after1"], scene.Log);
        Assert.All(scene.Steps.OfType<SceneStep>().Where(s => s.Name == "a"), s => Assert.Equal(StepState.Pending, s.State));
    }

    [Fact]
    public void SliderOnlyReportsMovesByHand()
    {
        var scene = new RecordingScene();
        scene.BuildSteps();
        Assert.Empty(scene.Log);

        var slider = scene.Steps.OfType<SliderStep>().Single();
        slider.Value = 7.4;
        Assert.Equal(7, slider.Value);
        Assert.Equal(["s7"], scene.Log);
        Assert.Equal("s: 7", slider.Text);
    }

    [Fact]
    public async Task SetUpOnlyStopsBeforeTheFirstTestStep()
    {
        var scene = new RecordingScene();
        scene.BuildSteps();
        scene.Log.Clear();

        await scene.RunAllAsync(setUpOnly: true);
        Assert.Equal(["setup"], scene.Log);
    }

    [Fact]
    public async Task ClickingAStepRunsItOnce()
    {
        var scene = new RecordingScene();
        scene.BuildSteps();
        scene.Log.Clear();

        var repeat = scene.Steps.OfType<SceneStep>().First(s => s.Name == "r");
        await repeat.RunAsync(automated: false);
        await repeat.RunAsync(automated: false);
        Assert.Equal(["r0", "r1"], scene.Log);
        Assert.Equal("r 2/3", repeat.Text);

        var toggle = scene.Steps.OfType<SceneStep>().First(s => s.Name == "t");
        await toggle.RunAsync(automated: false);
        Assert.True(toggle.Toggled);
        Assert.Equal("t: on", toggle.Text);
    }

    [Fact]
    public async Task UntilCountsTriesAndTimesOut()
    {
        var old = VisualScene.UntilTimeout;
        VisualScene.UntilTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var scene = new UntilScene();
            scene.BuildSteps();
            Assert.False(await scene.RunAllAsync());

            var until = (SceneStep)scene.Steps[0];
            Assert.Equal(StepState.Failed, until.State);
            Assert.Contains("timed out", until.Error);
            Assert.Matches(@"never \(\d+ tries\)", until.Text);
        }
        finally
        {
            VisualScene.UntilTimeout = old;
        }
    }

    private sealed class UntilScene : VisualScene
    {
        public override Task<Control> CreateAsync() => Task.FromResult<Control>(new Panel());

        [SetUpSteps]
        public void Wait() => AddUntilStep("never", () => false);
    }
}
