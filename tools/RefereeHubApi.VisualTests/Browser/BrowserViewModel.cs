using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Browser;

public sealed class SceneEntry(SceneInfo info, bool showHeader)
{
    public SceneInfo Info { get; } = info;

    public string Category => Info.Category.ToUpperInvariant();

    public string Name => Info.Name;

    public bool ShowHeader { get; } = showHeader;
}

public sealed partial class BrowserViewModel : ObservableObject
{
    // where the last picked scene is remembered between runs; null turns that off.
    public static string? LastSceneFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RefereeHub", "visualtests-last-scene");

    private VisualScene? scene;
    private int generation;
    private bool suppressLoad;

    public BrowserViewModel(string? initialScene = null)
    {
        refilter();
        string? wanted = initialScene ?? readLastScene();
        Selected = (wanted == null ? null : Entries.FirstOrDefault(e => e.Info == SceneCatalog.Find(wanted))) ?? Entries.FirstOrDefault();
    }

    public ObservableCollection<SceneEntry> Entries { get; } = new ObservableCollection<SceneEntry>();

    public ObservableCollection<StepBase> Steps { get; } = new ObservableCollection<StepBase>();

    public int SceneCount => SceneCatalog.All.Count;

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    partial void OnFilterChanged(string value) => refilter();

    [ObservableProperty]
    public partial SceneEntry? Selected { get; set; }

    partial void OnSelectedChanged(SceneEntry? value)
    {
        if (value != null && !suppressLoad)
            _ = loadAsync(value.Info, runSteps: AutoRun);
    }

    [ObservableProperty]
    public partial Control? Content { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; set; }

    public bool HasLoadError => LoadError != null;

    [ObservableProperty]
    public partial bool Busy { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    // on: picking a scene runs every step. off: only the first test's set-up runs, then step through by hand.
    [ObservableProperty]
    public partial bool AutoRun { get; set; } = true;

    // pause between steps, so each state can be seen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepDelayText))]
    public partial double StepDelayMs { get; set; } = VisualScene.StepDelay.TotalMilliseconds;

    partial void OnStepDelayMsChanged(double value) => VisualScene.StepDelay = TimeSpan.FromMilliseconds(Math.Round(value));

    public string StepDelayText => $"{StepDelayMs:0} ms";

    [ObservableProperty]
    public partial bool ListVisible { get; set; } = true;

    [RelayCommand]
    private void ToggleList() => ListVisible = !ListVisible;

    // Enter in the search box: open the first match.
    [RelayCommand]
    private void PickFirst()
    {
        if (Entries.Count > 0)
        {
            if (Selected == Entries[0])
                _ = loadAsync(Entries[0].Info, runSteps: AutoRun);
            else
                Selected = Entries[0];
        }
    }

    [RelayCommand]
    private Task RunAll() => Selected == null ? Task.CompletedTask : loadAsync(Selected.Info, runSteps: true);

    // rebuild the scene and run only the first test's set-up.
    [RelayCommand]
    private Task Reload() => Selected == null ? Task.CompletedTask : loadAsync(Selected.Info, runSteps: false);

    // one invocation of one step, whatever state the scene is in; nothing before it is re-run.
    [RelayCommand]
    private Task RunStep(SceneStep step) => exclusive(() => step.RunAsync(automated: false));

    // reset every step, then run this test's set-up and steps.
    [RelayCommand]
    private Task RunTest(LabelStep label) => exclusive(() => scene == null ? Task.CompletedTask : scene.RunTestAsync(label, cancelledFor(generation)));

    [RelayCommand]
    private void ResetSlider(SliderStep slider) => slider.ResetValue();

    private async Task exclusive(Func<Task> work)
    {
        if (Busy || scene == null)
            return;

        Busy = true;
        try
        {
            await work();
        }
        finally
        {
            Busy = false;
            summarise();
        }
    }

    private Func<bool> cancelledFor(int mine) => () => mine != generation;

    private void refilter()
    {
        var current = Selected;
        Entries.Clear();
        string? lastCategory = null;

        foreach (var s in SceneCatalog.All.Where(matches))
        {
            Entries.Add(new SceneEntry(s, s.Category != lastCategory));
            lastCategory = s.Category;
        }

        // keep the highlighted row if it survived the filter, without reloading it.
        if (current != null)
        {
            var again = Entries.FirstOrDefault(e => e.Info == current.Info);
            if (again != null)
            {
                suppressLoad = true;
                Selected = again;
                suppressLoad = false;
            }
        }
    }

    private bool matches(SceneInfo s) => Filter.Length == 0
                                         || s.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                         || s.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    private async Task loadAsync(SceneInfo info, bool runSteps)
    {
        int mine = ++generation;
        Busy = true;
        LoadError = null;
        Title = $"{info.Category} / {info.Name}";
        Description = info.Description;
        Steps.Clear();
        Summary = string.Empty;

        var old = scene;
        scene = null;
        Content = null;
        if (old != null)
            await old.DisposeAsync();

        if (mine != generation)
            return;

        saveLastScene(info);

        var fresh = info.Create();
        try
        {
            Content = await fresh.CreateAsync();
            fresh.BuildSteps();
        }
        catch (Exception ex)
        {
            LoadError = $"Scene failed to set up: {ex.GetType().Name}: {ex.Message}";
            Busy = false;
            await fresh.DisposeAsync();
            return;
        }

        if (mine != generation)
        {
            await fresh.DisposeAsync();
            return;
        }

        scene = fresh;
        foreach (var s in fresh.Steps)
            Steps.Add(s);

        try
        {
            await fresh.RunAllAsync(cancelledFor(mine), setUpOnly: !runSteps);
        }
        finally
        {
            if (mine == generation)
            {
                Busy = false;
                summarise();
            }
        }
    }

    private void summarise()
    {
        var runnable = Steps.OfType<SceneStep>().ToList();
        int passed = runnable.Count(s => s.State == StepState.Passed);
        int failed = runnable.Count(s => s.State == StepState.Failed);
        int tests = Steps.OfType<LabelStep>().Count();
        string head = tests > 0 ? $"{tests} tests, " : string.Empty;
        Summary = head + (failed > 0 ? $"{failed} failed, {passed}/{runnable.Count} steps passed" : $"{passed}/{runnable.Count} steps passed");
    }

    private static string? readLastScene()
    {
        try
        {
            return LastSceneFile != null && File.Exists(LastSceneFile) ? File.ReadAllText(LastSceneFile).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void saveLastScene(SceneInfo info)
    {
        if (LastSceneFile == null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastSceneFile)!);
            File.WriteAllText(LastSceneFile, info.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task ShutdownAsync()
    {
        generation++;
        if (scene != null)
            await scene.DisposeAsync();
        scene = null;
    }
}
