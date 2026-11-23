using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RefereeHubApi.Desktop.Services;

namespace RefereeHubApi.VisualTests.Framework;

public enum StepKind
{
    Action,
    Repeat,
    Wait,
    Until,
    Assert,
    Toggle,
}

public enum StepState
{
    Pending,
    Running,
    Passed,
    Failed,
}

// a row in the step list: a label, a runnable step, or a slider.
public abstract partial class StepBase : ObservableObject
{
    protected StepBase(string name, bool isSetUp)
    {
        Name = name;
        IsSetUp = isSetUp;
    }

    public string Name { get; }

    // added by a [SetUpSteps] method.
    public bool IsSetUp { get; }

    public virtual void Reset()
    {
    }
}

// heads one [Test] method's steps; clicking it runs just that test.
public sealed class LabelStep(string name) : StepBase(name, false);

// interactive only: automated runs leave it alone, like osu-framework's slider steps.
public sealed partial class SliderStep : StepBase
{
    private readonly Action<double> changed;
    private readonly bool ready;

    public SliderStep(string name, double min, double max, double start, bool whole, Action<double> changed, bool isSetUp)
        : base(name, isSetUp)
    {
        Min = min;
        Max = max;
        Start = start;
        Whole = whole;
        this.changed = changed;
        Value = start;
        ready = true;
    }

    public double Min { get; }

    public double Max { get; }

    public double Start { get; }

    public bool Whole { get; }

    public double SmallChange => Whole ? 1 : (Max - Min) / 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial double Value { get; set; }

    partial void OnValueChanged(double value)
    {
        if (Whole && value != Math.Round(value))
        {
            Value = Math.Round(value);
            return;
        }

        // only moves made after the steps exist reach the scene; the start value is just shown.
        if (ready)
            changed(value);
    }

    public string Text => $"{Name}: {Value:#,0.##}";

    public void ResetValue() => Value = Start;
}

public sealed partial class SceneStep : StepBase
{
    private static readonly IBrush action_light = Ui.Brush("#8a2be2");
    private static readonly IBrush until_light = Ui.Brush("#a0522d");
    private static readonly IBrush assert_light = Ui.Brush("#4f8fd1");
    private static readonly IBrush setup_light = Ui.Brush("#3fb8a8");
    private static readonly IBrush success_light = Ui.Brush("#9acd32");
    private static readonly IBrush failure_light = Ui.Brush("#ff3344");
    private static readonly IBrush idle_bg = Brushes.Transparent;
    private static readonly IBrush running_bg = Ui.Brush("#2f3a35");
    private static readonly IBrush failed_bg = Ui.Brush("#4a2424");

    private readonly Func<int, Task> perform;

    public SceneStep(StepKind kind, string name, int count, Func<int, Task> perform, bool isSetUp)
        : base(name, isSetUp)
    {
        Kind = kind;
        Count = count;
        this.perform = perform;
    }

    public StepKind Kind { get; }

    // repetitions for repeat/wait steps.
    public int Count { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial int Invocations { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Light), nameof(Background))]
    public partial StepState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Light), nameof(Text))]
    public partial bool Toggled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error != null;

    public string Prefix => Kind switch
    {
        _ when IsSetUp => "setup",
        StepKind.Until => "until",
        StepKind.Assert => "assert",
        StepKind.Toggle => "toggle",
        StepKind.Wait => "wait",
        StepKind.Repeat => "repeat",
        _ => string.Empty,
    };

    public string Text => Kind switch
    {
        StepKind.Repeat or StepKind.Wait => $"{Name} {Invocations}/{Count}",
        StepKind.Until when Invocations > 0 => $"{Name} ({Invocations} {(Invocations == 1 ? "try" : "tries")})",
        StepKind.Toggle => $"{Name}: {(Toggled ? "on" : "off")}",
        _ => Name,
    };

    public IBrush Light => State switch
    {
        StepState.Failed => failure_light,
        StepState.Passed => success_light,
        _ when Kind == StepKind.Toggle => Toggled ? success_light : failure_light,
        _ when IsSetUp => setup_light,
        _ => Kind switch
        {
            StepKind.Until => until_light,
            StepKind.Assert => assert_light,
            _ => action_light,
        },
    };

    public IBrush Background => State switch
    {
        StepState.Running => running_bg,
        StepState.Failed => failed_bg,
        _ => idle_bg,
    };

    public override void Reset()
    {
        State = StepState.Pending;
        Error = null;
        Invocations = 0;
    }

    // automated: the whole step (all repetitions, both toggle states, polling until true).
    // by hand: a single invocation, like clicking a step button in osu-framework.
    public async Task<bool> RunAsync(bool automated)
    {
        State = StepState.Running;
        Error = null;

        try
        {
            switch (Kind)
            {
                case StepKind.Repeat or StepKind.Wait:
                    if (!automated)
                    {
                        await invoke();
                        break;
                    }

                    Invocations = 0;
                    while (Invocations < Count)
                    {
                        if (Invocations > 0)
                            await VisualScene.DelayAsync();
                        await invoke();
                    }

                    break;

                case StepKind.Toggle:
                    if (automated)
                    {
                        Toggled = true;
                        await perform(1);
                        await VisualScene.DelayAsync();
                        Toggled = false;
                        await perform(0);
                    }
                    else
                    {
                        Toggled = !Toggled;
                        await perform(Toggled ? 1 : 0);
                    }

                    break;

                default:
                    Invocations = 0;
                    await perform(0);
                    break;
            }

            State = StepState.Passed;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex is StepFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            State = StepState.Failed;
            return false;
        }
    }

    private async Task invoke()
    {
        await perform(Invocations);
        Invocations++;
    }

    // until steps report each poll so the button can show its try count.
    internal void CountTry() => Invocations++;
}

public sealed class StepFailedException(string message) : Exception(message);
