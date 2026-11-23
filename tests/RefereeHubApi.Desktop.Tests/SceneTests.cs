using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using RefereeHubApi.Desktop;
using RefereeHubApi.VisualTests.Browser;
using RefereeHubApi.VisualTests.Framework;

[assembly: AvaloniaTestApplication(typeof(RefereeHubApi.Desktop.Tests.TestApp))]

namespace RefereeHubApi.Desktop.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UseSkia()
                     .AfterSetup(_ => Program.UseInvariantNumbers())
                     .WithInterFont()
                     .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

// runs every visual test scene headless, all steps in order, against the fake hub; fails on a
// failed step or any binding error. Set REFEREEHUB_SNAPSHOTS=<dir> to keep a PNG of each scene's end state.
// Browse the same scenes interactively: dotnet run --project tools/RefereeHubApi.VisualTests
// VisualScene timing settings are static, so these classes take turns.
[Collection("visual scenes")]
public sealed class SceneTests : IDisposable
{
    private readonly BindingErrors errors = new BindingErrors();

    public SceneTests()
    {
        Logger.Sink = errors;
        VisualScene.StepDelay = TimeSpan.Zero;
        VisualScene.Pump = async () =>
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        };
    }

    public static TheoryData<string> Scenes => new TheoryData<string>(SceneCatalog.All.Select(s => s.ToString()));

    [Fact]
    public void CatalogFindsScenes() => Assert.NotEmpty(SceneCatalog.All);

    [AvaloniaTheory]
    [MemberData(nameof(Scenes))]
    public async Task Scene(string name)
    {
        var info = SceneCatalog.Find(name) ?? throw new InvalidOperationException($"No scene {name}");
        await using var scene = info.Create();

        var window = new Window
        {
            Width = scene.PreferredSize.Width,
            Height = scene.PreferredSize.Height,
            Content = await scene.CreateAsync(),
        };
        window.Show();
        scene.BuildSteps();

        try
        {
            await scene.RunAllAsync();
            Dispatcher.UIThread.RunJobs();

            var failed = scene.Steps.OfType<SceneStep>().FirstOrDefault(s => s.State != StepState.Passed);
            if (failed != null)
            {
                int at = scene.Steps.ToList().IndexOf(failed);
                string test = scene.Steps.Take(at).OfType<LabelStep>().LastOrDefault()?.Name ?? "(set-up)";
                Assert.Fail($"{test}: step \"{failed.Name}\" {failed.State}: {failed.Error}");
            }

            snapshot(window, info);
            Assert.True(errors.Messages.Count == 0, string.Join("\n", errors.Messages));
        }
        finally
        {
            window.Close();
        }
    }

    // the interactive browser itself: picks a scene, auto-runs it, steps all pass.
    [AvaloniaFact]
    public async Task BrowserRunsPickedScene()
    {
        BrowserViewModel.LastSceneFile = null;
        var vm = new BrowserViewModel("Chat and highlights");
        var window = new BrowserWindow { DataContext = vm, Width = 1760, Height = 980 };
        window.Show();

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((vm.Busy || vm.Steps.Count == 0) && DateTime.UtcNow < deadline)
                await VisualScene.Pump();

            Assert.Equal("Chat and highlights", vm.Selected?.Name);
            Assert.All(vm.Steps.OfType<SceneStep>(), s => Assert.Equal(StepState.Passed, s.State));
            Assert.Equal(5, vm.Steps.OfType<LabelStep>().Count());
            Assert.Equal(3, vm.Steps.OfType<SliderStep>().Count());
            Assert.IsType<RefereeHubApi.Desktop.Views.MainView>(Assert.IsType<ContentControl>(vm.Content).Content);

            vm.Filter = "highlights";
            Assert.Single(vm.Entries);
            Assert.Same(vm.Entries[0], vm.Selected);

            snapshot(window, "browser");
            Assert.True(errors.Messages.Count == 0, string.Join("\n", errors.Messages));
        }
        finally
        {
            await vm.ShutdownAsync();
            window.Close();
        }
    }

    private static void snapshot(Window window, SceneInfo info) => snapshot(window, $"{info.Category}-{info.Name}");

    private static void snapshot(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        string? dir = Environment.GetEnvironmentVariable("REFEREEHUB_SNAPSHOTS");
        if (dir == null)
            return;

        Directory.CreateDirectory(dir);
        string file = name.ToLowerInvariant().Replace(' ', '-');
        frame.Save(Path.Combine(dir, file + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    public void Dispose() => Logger.Sink = null;

    private sealed class BindingErrors : ILogSink
    {
        public List<string> Messages { get; } = new List<string>();

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                lock (Messages)
                    Messages.Add($"[{area}] {source?.GetType().Name}: {messageTemplate} {string.Join(", ", propertyValues)}");
            }
        }
    }
}
