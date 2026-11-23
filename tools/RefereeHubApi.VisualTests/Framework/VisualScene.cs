using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

namespace RefereeHubApi.VisualTests.Framework;

// a screen someone can look at in known states, after osu-framework's TestScene. CreateAsync builds
// the content once; [Test] / [TestCase] methods add steps under a label each, with [SetUpSteps] before
// every test. Category comes from the scene's namespace.
public abstract partial class VisualScene : IAsyncDisposable
{
    private readonly List<StepBase> steps = new List<StepBase>();
    private bool addingSetUp;

    // how long Until steps wait before failing.
    public static TimeSpan UntilTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // pause between steps and repetitions so each state can be seen; the headless runner sets zero.
    public static TimeSpan StepDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    // yields to the UI between polls; the headless runner swaps in one that also pumps the dispatcher.
    public static Func<Task> Pump { get; set; } = () => Task.Delay(20);

    public static async Task DelayAsync()
    {
        if (StepDelay > TimeSpan.Zero)
            await Task.Delay(StepDelay);
        await Pump();
    }

    public virtual string Category => humanise(GetType().Namespace?.Split('.').Last() ?? "Misc");

    public virtual string Name => humanise(GetType().Name.EndsWith("Scene") ? GetType().Name[..^5] : GetType().Name);

    public virtual string Description => string.Empty;

    // size the headless runner renders at; the browser fills whatever space it has.
    public virtual Size PreferredSize => new Size(1440, 980);

    public IReadOnlyList<StepBase> Steps => steps;

    public abstract Task<Control> CreateAsync();

    // label, set-up steps, test steps, for every test method (or just set-up when there are none).
    public void BuildSteps()
    {
        steps.Clear();

        var methods = GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var setUps = methods.Where(m => m.GetCustomAttribute<SetUpStepsAttribute>() != null)
                            .OrderBy(m => depth(m.DeclaringType!))
                            .ToList();

        var tests = new List<(string Name, MethodInfo Method, object?[] Args)>();
        foreach (var m in methods.OrderBy(m => m.MetadataToken))
        {
            string name = m.Name.StartsWith("Test") && m.Name.Length > 4 ? m.Name[4..] : m.Name;
            if (m.GetCustomAttribute<TestAttribute>() != null)
                tests.Add((humanise(name), m, []));
            foreach (var c in m.GetCustomAttributes<TestCaseAttribute>())
                tests.Add(($"{humanise(name)}({string.Join(", ", c.Arguments.Select(a => a is string str ? $"\"{str}\"" : a?.ToString() ?? "null"))})", m, c.Arguments));
        }

        if (tests.Count == 0)
        {
            addSetUp(setUps);
            return;
        }

        foreach (var (name, method, args) in tests)
        {
            steps.Add(new LabelStep(name));
            addSetUp(setUps);
            method.Invoke(this, args);
        }
    }

    private void addSetUp(List<MethodInfo> setUps)
    {
        addingSetUp = true;
        try
        {
            foreach (var s in setUps)
                s.Invoke(this, []);
        }
        finally
        {
            addingSetUp = false;
        }
    }

    private static int depth(Type t)
    {
        int d = 0;
        for (var b = t.BaseType; b != null; b = b.BaseType)
            d++;
        return d;
    }

    protected void AddLabel(string name) => steps.Add(new LabelStep(name));

    protected void AddStep(string name, Action action) => AddStep(name, () =>
    {
        action();
        return Task.CompletedTask;
    });

    protected void AddStep(string name, Func<Task> action) => add(StepKind.Action, name, 1, _ => action());

    // runs `count` times in automated runs, StepDelay apart; each click runs it once more.
    protected void AddRepeatStep(string name, Action<int> action, int count) => add(StepKind.Repeat, name, count, i =>
    {
        action(i);
        return Task.CompletedTask;
    });

    protected void AddRepeatStep(string name, Func<int, Task> action, int count) => add(StepKind.Repeat, name, count, action);

    // lets `count` step delays pass, so an animation or clock can be watched.
    protected void AddWaitStep(string name, int count) => add(StepKind.Wait, name, count, _ => Task.CompletedTask);

    // automated runs switch it on, then off; clicking flips it.
    protected void AddToggleStep(string name, Action<bool> action) => add(StepKind.Toggle, name, 1, v =>
    {
        action(v == 1);
        return Task.CompletedTask;
    });

    // only moved by hand; automated runs skip it.
    protected void AddSliderStep(string name, double min, double max, double start, Action<double> changed)
        => steps.Add(new SliderStep(name, min, max, start, false, changed, addingSetUp));

    protected void AddSliderStep(string name, int min, int max, int start, Action<int> changed)
        => steps.Add(new SliderStep(name, min, max, start, true, v => changed((int)v), addingSetUp));

    protected void AddUntilStep(string name, Func<bool> condition)
    {
        SceneStep? step = null;
        step = add(StepKind.Until, name, 1, async _ =>
        {
            var deadline = DateTime.UtcNow + UntilTimeout;
            while (true)
            {
                step!.CountTry();
                if (condition())
                    return;
                if (DateTime.UtcNow > deadline)
                    throw new StepFailedException($"\"{name}\" timed out after {UntilTimeout.TotalSeconds:0}s");
                await Pump();
            }
        });
    }

    protected void AddAssert(string name, Func<bool> check) => add(StepKind.Assert, name, 1, async _ =>
    {
        // let posted UI updates land before checking.
        await Pump();
        if (!check())
            throw new StepFailedException($"Assertion failed: {name}");
    });

    private SceneStep add(StepKind kind, string name, int count, Func<int, Task> perform)
    {
        var s = new SceneStep(kind, name, count, perform, addingSetUp);
        steps.Add(s);
        return s;
    }

    public void ResetSteps()
    {
        foreach (var s in steps)
            s.Reset();
    }

    // every runnable step in order (labels and sliders skipped), stopping at the first failure.
    // setUpOnly: stop before the first test step, leaving the scene ready to be stepped by hand.
    public Task<bool> RunAllAsync(Func<bool>? cancelled = null, bool setUpOnly = false)
        => runAsync(0, steps.Count, cancelled, setUpOnly);

    // one test: everything between its label and the next one.
    public Task<bool> RunTestAsync(LabelStep label, Func<bool>? cancelled = null)
    {
        int start = steps.IndexOf(label) + 1;
        int end = steps.FindIndex(start, s => s is LabelStep);
        return runAsync(start, end < 0 ? steps.Count : end, cancelled, false);
    }

    private async Task<bool> runAsync(int start, int end, Func<bool>? cancelled, bool setUpOnly)
    {
        ResetSteps();

        bool first = true;
        for (int i = start; i < end; i++)
        {
            if (steps[i] is not SceneStep s)
                continue;
            if (setUpOnly && !s.IsSetUp)
                return true;
            if (cancelled?.Invoke() == true)
                return false;

            if (!first)
                await DelayAsync();
            first = false;

            if (!await s.RunAsync(automated: true))
                return false;
        }

        return true;
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string humanise(string pascal)
    {
        string spaced = SplitWords().Replace(pascal, " $1").Trim();
        return spaced.Length == 0 ? pascal : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex SplitWords();
}
