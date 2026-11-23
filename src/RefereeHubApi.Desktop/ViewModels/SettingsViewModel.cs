using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.Api;
using RefereeHubApi.Commands;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Logs;
using RefereeHubApi.Settings;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel main;
    private bool loading;

    // the window skips keybinds while a key is being recorded.
    public static bool CapturingKey { get; private set; }

    public SettingsViewModel(MainWindowViewModel main)
    {
        this.main = main;
        load();
    }

    private RefereeSettings settings => main.Services.Settings;

    public string FilePath => main.Services.SettingsStore.FilePath;

    // ---- highlights ----

    [ObservableProperty]
    public partial bool Mentions { get; set; }

    [ObservableProperty]
    public partial bool RefereeCalls { get; set; }

    [ObservableProperty]
    public partial bool PrivateMessages { get; set; }

    public ObservableCollection<KeywordViewModel> Keywords { get; } = new ObservableCollection<KeywordViewModel>();

    [ObservableProperty]
    public partial string NewKeyword { get; set; } = string.Empty;

    partial void OnMentionsChanged(bool value) => saveHighlights();

    partial void OnRefereeCallsChanged(bool value) => saveHighlights();

    partial void OnPrivateMessagesChanged(bool value) => saveHighlights();

    [RelayCommand]
    private void AddKeyword()
    {
        string word = NewKeyword.Trim();
        if (word.Length == 0 || Keywords.Any(k => k.Word.Equals(word, StringComparison.OrdinalIgnoreCase)))
            return;

        Keywords.Add(new KeywordViewModel(this, word));
        NewKeyword = string.Empty;
        saveHighlights();
    }

    internal void RemoveKeyword(KeywordViewModel k)
    {
        Keywords.Remove(k);
        saveHighlights();
    }

    private void saveHighlights()
    {
        if (loading)
            return;

        var h = settings.Highlights;
        h.Mentions = Mentions;
        h.RefereeCalls = RefereeCalls;
        h.PrivateMessages = PrivateMessages;
        h.Keywords = Keywords.Select(k => k.Word).ToArray();
        if (main.Session != null)
            main.Session.HighlightRules = h;
        save();
    }

    // ---- sounds (stored only; the app has no audio output yet) ----

    public string[] SoundOptions { get; } = ["none", "ping", "chime", "alarm"];

    public ObservableCollection<AlertViewModel> Alerts { get; } = new ObservableCollection<AlertViewModel>();

    internal void SetAlert(string trigger, string sound)
    {
        if (loading)
            return;

        if (sound == "none")
            settings.Alerts.Remove(trigger);
        else
            settings.Alerts[trigger] = sound;
        save();
    }

    // ---- macros ----

    public ObservableCollection<MacroViewModel> Macros { get; } = new ObservableCollection<MacroViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMacro))]
    public partial MacroViewModel? SelectedMacro { get; set; }

    public bool HasSelectedMacro => SelectedMacro != null;

    [ObservableProperty]
    public partial string MacroText { get; set; } = string.Empty;

    public ObservableCollection<MacroLineViewModel> MacroLines { get; } = new ObservableCollection<MacroLineViewModel>();

    [ObservableProperty]
    public partial string NewMacroName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetKeyLabel))]
    public partial bool Capturing { get; set; }

    public string SetKeyLabel => Capturing ? "Press a key…" : "Set key";

    partial void OnCapturingChanged(bool value) => CapturingKey = value;

    partial void OnSelectedMacroChanged(MacroViewModel? value)
    {
        loading = true;
        MacroText = value == null ? string.Empty : string.Join(Environment.NewLine, settings.Macros.GetValueOrDefault(value.Name) ?? []);
        loading = false;
        validate();
    }

    partial void OnMacroTextChanged(string value)
    {
        validate();
        if (loading || SelectedMacro == null)
            return;

        settings.Macros[SelectedMacro.Name] = lines(value);
        SelectedMacro.Refresh(settings);
        save();
    }

    private static List<string> lines(string text)
        => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private void validate()
    {
        MacroLines.Clear();
        int n = 0;
        foreach (string line in lines(MacroText))
        {
            n++;
            string? error = null;
            try
            {
                var step = main.Services.Commands.Parse(line);
                if (step.IsMacro)
                    error = "macros can't run other macros";
            }
            catch (CommandException ex)
            {
                error = ex.Message;
            }

            MacroLines.Add(new MacroLineViewModel(n, line, error));
        }
    }

    [RelayCommand]
    private void NewMacro()
    {
        string name = NewMacroName.Trim();
        if (name.Length == 0 || name.Contains(' ') || settings.Macros.ContainsKey(name))
        {
            main.Services.ReportError("Macro names must be unique and have no spaces.");
            return;
        }

        settings.Macros[name] = new List<string>();
        var vm = new MacroViewModel(name);
        vm.Refresh(settings);
        Macros.Add(vm);
        SelectedMacro = vm;
        NewMacroName = string.Empty;
        save();
    }

    [RelayCommand]
    private void DeleteMacro()
    {
        if (SelectedMacro is not { } m)
            return;

        settings.Macros.Remove(m.Name);
        foreach (var key in settings.Keybinds.Where(k => k.Value == m.Name).Select(k => k.Key).ToArray())
            settings.Keybinds.Remove(key);
        Macros.Remove(m);
        SelectedMacro = Macros.FirstOrDefault();
        save();
    }

    [RelayCommand]
    private void SetKey() => Capturing = SelectedMacro != null && !Capturing;

    [RelayCommand]
    private void ClearKey()
    {
        if (SelectedMacro is not { } m)
            return;

        foreach (var key in settings.Keybinds.Where(k => k.Value == m.Name).Select(k => k.Key).ToArray())
            settings.Keybinds.Remove(key);
        m.Refresh(settings);
        save();
    }

    public void KeyCaptured(string gesture)
    {
        Capturing = false;
        if (SelectedMacro is not { } m)
            return;

        foreach (var key in settings.Keybinds.Where(k => k.Value == m.Name).Select(k => k.Key).ToArray())
            settings.Keybinds.Remove(key);
        settings.Keybinds[gesture] = m.Name;
        foreach (var other in Macros)
            other.Refresh(settings);
        save();
    }

    // ---- timer announcements ----

    [ObservableProperty]
    public partial bool AnnounceTimer { get; set; }

    [ObservableProperty]
    public partial bool AnnounceStart { get; set; }

    public ObservableCollection<MilestoneViewModel> Milestones { get; } = new ObservableCollection<MilestoneViewModel>();

    [ObservableProperty]
    public partial string NewMilestone { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StartedMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MilestoneMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FinishedMessage { get; set; } = string.Empty;

    partial void OnAnnounceTimerChanged(bool value) => saveTimer();

    partial void OnAnnounceStartChanged(bool value) => saveTimer();

    partial void OnStartedMessageChanged(string value) => saveTimer();

    partial void OnMilestoneMessageChanged(string value) => saveTimer();

    partial void OnFinishedMessageChanged(string value) => saveTimer();

    [RelayCommand]
    private void AddMilestone()
    {
        try
        {
            int seconds = (int)new CommandArgs(NewMilestone.Trim(), "milestone <duration>").Duration(0).TotalSeconds;
            if (Milestones.All(m => m.Seconds != seconds))
            {
                var sorted = Milestones.Select(m => m.Seconds).Append(seconds).OrderByDescending(s => s).ToArray();
                Milestones.Clear();
                foreach (int s in sorted)
                    Milestones.Add(new MilestoneViewModel(this, s));
            }

            NewMilestone = string.Empty;
            saveTimer();
        }
        catch (CommandException ex)
        {
            main.Services.ReportError(ex);
        }
    }

    internal void RemoveMilestone(MilestoneViewModel m)
    {
        Milestones.Remove(m);
        saveTimer();
    }

    private void saveTimer()
    {
        if (loading)
            return;

        var t = settings.Timer;
        t.Enabled = AnnounceTimer;
        t.AnnounceStart = AnnounceStart;
        t.MilestoneSeconds = Milestones.Select(m => m.Seconds).ToArray();
        t.StartedMessage = StartedMessage;
        t.MilestoneMessage = MilestoneMessage;
        t.FinishedMessage = FinishedMessage;
        save();
    }

    // ---- chat log ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UtcBg), nameof(UtcFg), nameof(LocalBg), nameof(LocalFg))]
    public partial bool UseUtc { get; set; }

    [ObservableProperty]
    public partial bool IncludeResults { get; set; }

    [ObservableProperty]
    public partial string LogPreview { get; set; } = string.Empty;

    public IBrush UtcBg => UseUtc ? Ui.Brush("#eef3f0") : Brushes.Transparent;

    public IBrush UtcFg => UseUtc ? Ui.Brush("#0d1210") : Ui.Brush("#c6d3cd");

    public IBrush LocalBg => !UseUtc ? Ui.Brush("#eef3f0") : Brushes.Transparent;

    public IBrush LocalFg => !UseUtc ? Ui.Brush("#0d1210") : Ui.Brush("#c6d3cd");

    [RelayCommand]
    private void PickUtc() => UseUtc = true;

    [RelayCommand]
    private void PickLocal() => UseUtc = false;

    partial void OnUseUtcChanged(bool value) => saveLog();

    partial void OnIncludeResultsChanged(bool value) => saveLog();

    private void saveLog()
    {
        if (!loading)
        {
            settings.ChatLog.TimeZone = UseUtc ? ChatLogTimeZone.Utc : ChatLogTimeZone.Local;
            settings.ChatLog.IncludeResults = IncludeResults;
            save();
        }

        buildPreview();
    }

    private void buildPreview()
    {
        var room = main.ActiveRoom?.Room ?? main.RoomTabs.FirstOrDefault()?.Room;
        if (room != null && room.Transcript.Messages.Count > 0)
        {
            string full = room.ExportLog(settings.ChatLog);
            LogPreview = string.Join('\n', full.Split('\n').Take(8));
            return;
        }

        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var t = new ChatTranscript(1, "TEST: (Team A) vs (Team B)", start);
        t.Add(new ChatMessage { MessageId = 1, SenderId = 1, Timestamp = start.AddMinutes(1), Content = "we ban HD3", Sender = new ApiUser { Id = 1, Username = "captain_a" } });
        t.Add(new ChatMessage { MessageId = 2, SenderId = 2, Timestamp = start.AddMinutes(29), Content = "Team A 3 - 2 Team B. Team B to pick.", Sender = new ApiUser { Id = 2, Username = "referee" } });
        LogPreview = ChatLogFormatter.Format(t, settings.ChatLog);
    }

    // ---- lifecycle ----

    public void OnConnected() => buildPreview();

    private void load()
    {
        loading = true;
        var s = settings;

        Mentions = s.Highlights.Mentions;
        RefereeCalls = s.Highlights.RefereeCalls;
        PrivateMessages = s.Highlights.PrivateMessages;
        Keywords.Clear();
        foreach (string k in s.Highlights.Keywords)
            Keywords.Add(new KeywordViewModel(this, k));

        Alerts.Clear();
        foreach (string trigger in AlertTriggers.ALL)
            Alerts.Add(new AlertViewModel(this, trigger, s.Alerts.GetValueOrDefault(trigger, "none")));

        Macros.Clear();
        foreach (string name in s.Macros.Keys.OrderBy(n => n))
        {
            var m = new MacroViewModel(name);
            m.Refresh(s);
            Macros.Add(m);
        }

        AnnounceTimer = s.Timer.Enabled;
        AnnounceStart = s.Timer.AnnounceStart;
        Milestones.Clear();
        foreach (int sec in s.Timer.MilestoneSeconds.OrderByDescending(x => x))
            Milestones.Add(new MilestoneViewModel(this, sec));
        StartedMessage = s.Timer.StartedMessage;
        MilestoneMessage = s.Timer.MilestoneMessage;
        FinishedMessage = s.Timer.FinishedMessage;

        UseUtc = s.ChatLog.TimeZone == ChatLogTimeZone.Utc;
        IncludeResults = s.ChatLog.IncludeResults;
        loading = false;

        SelectedMacro = Macros.FirstOrDefault();
        buildPreview();
    }

    private void save() => main.Services.SaveSettingsSoon();
}

public sealed partial class KeywordViewModel(SettingsViewModel owner, string word) : ObservableObject
{
    public string Word { get; } = word;

    public string RemoveLabel => $"Remove keyword {Word}";

    [RelayCommand]
    private void Remove() => owner.RemoveKeyword(this);
}

public sealed partial class AlertViewModel : ObservableObject
{
    private static readonly Dictionary<string, (string Label, string Color)> labels = new Dictionary<string, (string, string)>
    {
        [AlertTriggers.MENTION] = ("Mention", "#ff66ab"),
        [AlertTriggers.REFEREE_CALL] = ("Ref call", "#ffcc33"),
        [AlertTriggers.KEYWORD] = ("Keyword", "#ffcc33"),
        [AlertTriggers.PRIVATE_MESSAGE] = ("Private message", "#7fe3d4"),
        [AlertTriggers.TIMER_MILESTONE] = ("Pick timer milestone", "#5fe0d0"),
        [AlertTriggers.TIMER_FINISHED] = ("Pick timer finished", "#5fe0d0"),
        [AlertTriggers.MATCH_COMPLETED] = ("Map finished", "#9be34a"),
        [AlertTriggers.SERVER_SHUTTING_DOWN] = ("Server restarting", "#ff4d5e"),
        [AlertTriggers.PLAYER_JOINED] = ("Player joined", "#c6d3cd"),
    };

    private readonly SettingsViewModel owner;

    public AlertViewModel(SettingsViewModel owner, string trigger, string sound)
    {
        this.owner = owner;
        Trigger = trigger;
        var (label, color) = labels.GetValueOrDefault(trigger, (trigger, "#c6d3cd"));
        Label = label;
        Color = Ui.Brush(color);
        Sound = owner.SoundOptions.Contains(sound) ? sound : "none";
    }

    public string Trigger { get; }

    public string Label { get; }

    public IBrush Color { get; }

    public string[] Options => owner.SoundOptions;

    [ObservableProperty]
    public partial string Sound { get; set; }

    partial void OnSoundChanged(string value) => owner.SetAlert(Trigger, value);
}

public sealed partial class MacroViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Steps { get; set; } = string.Empty;

    public void Refresh(RefereeSettings settings)
    {
        Key = settings.Keybinds.FirstOrDefault(k => k.Value == Name).Key ?? "—";
        int n = settings.Macros.GetValueOrDefault(Name)?.Count ?? 0;
        Steps = n == 1 ? "1 step" : $"{n} steps";
    }
}

public sealed record MacroLineViewModel(int Number, string Text, string? Error)
{
    public bool Bad => Error != null;

    public IBrush Bar => Bad ? Ui.Brush("#ff4d5e") : Ui.Brush("#2a3530");
}

public sealed partial class MilestoneViewModel(SettingsViewModel owner, int seconds) : ObservableObject
{
    public int Seconds { get; } = seconds;

    public string Label => Ui.Clock(TimeSpan.FromSeconds(Seconds));

    [RelayCommand]
    private void Remove() => owner.RemoveMilestone(this);
}
