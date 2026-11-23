using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.Api;
using RefereeHubApi.Beatmaps;
using RefereeHubApi.Commands;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Highlights;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Session;
using RefereeHubApi.State;
using RefereeHubApi.Users;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class RoomViewModel : ViewModelBase, IDisposable
{
    private static readonly Regex team_names = new Regex(@"\((.+?)\)\s*vs\.?\s*\((.+?)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly int[] best_of_options = [1, 3, 5, 7, 9, 11, 13];

    private readonly List<IDisposable> subscriptions = new List<IDisposable>();
    private readonly HashSet<long> seenMessages = new HashSet<long>();
    private readonly Dictionary<long, HighlightKind> pendingHighlights = new Dictionary<long, HighlightKind>();
    private readonly Dictionary<int, ApiBeatmap> beatmaps = new Dictionary<int, ApiBeatmap>();
    private readonly HashSet<int> requestedUsers = new HashSet<int>();
    private readonly HashSet<int> requestedBeatmaps = new HashSet<int>();
    private readonly CommandContext commandContext;
    private string? currentKey;
    private CancellationTokenSource? describe;

    public RoomViewModel(MainWindowViewModel main, RefereeRoom room)
    {
        Main = main;
        Room = room;
        commandContext = main.Services.CreateCommandContext(room, AddSystemLine);

        Countdowns = new[] { 0, 10, 30, 60, 120 }.Select(s => new CountdownOption(this, s)).ToArray();
        TimerPresets = ["30s", "1m", "1:30", "2m", "3m", "5m"];

        subscriptions.Add(room.StateChanged.OnUi(apply));
        subscriptions.Add(room.Events.OnUi(onEvent));
        subscriptions.Add(room.Results.OnUi(onResult));
        subscriptions.Add(room.Transcript.Changed.OnUi(_ => syncChat()));
        subscriptions.Add(room.Timer.Ticks.OnUi(t =>
        {
            TimerRunning = true;
            TimerText = Ui.Clock(t.Remaining);
        }));
        subscriptions.Add(room.Timer.Stopped.OnUi(_ => TimerRunning = false));
        subscriptions.Add(room.Timer.Finished.OnUi(_ => TimerRunning = false));

        if (room.ChatError != null)
            AddSystemLine($"Chat unavailable: {room.ChatError.Message}");

        syncChat();
        apply(room.State);
    }

    public MainWindowViewModel Main { get; }

    public RefereeRoom Room { get; }

    public long RoomId => Room.RoomId;

    public IBrush Accent => Main.Accent;

    private RefereeSession? session => Main.Session;

    // ---- tab ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabBackground), nameof(TabLine), nameof(TabForeground))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;

    public IBrush TabBackground => IsActive ? Ui.Brush("#1c2420") : Brushes.Transparent;

    public IBrush TabLine => IsActive ? Accent : Brushes.Transparent;

    public IBrush TabForeground => IsActive ? Ui.Brush("#eef3f0") : Ui.Brush("#9fb0a8");

    [ObservableProperty]
    public partial string TabTitle { get; set; } = string.Empty;

    public void ClearBadge() => Badge = 0;

    // ---- header ----

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    public string IdText => $"#{RoomId}";

    [ObservableProperty]
    public partial string TypeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QueueText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText), nameof(LockIcon), nameof(LockBg))]
    public partial bool Locked { get; set; }

    public string LockText => Locked ? "Locked" : "Unlocked";

    public string LockIcon => Locked ? "lock" : "unlock";

    public IBrush LockBg => Locked ? Ui.Brush("#6f4fd8") : Ui.Brush("#3a3150");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordText), nameof(HasPassword))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordText), nameof(PasswordToggleLabel))]
    public partial bool PasswordShown { get; set; }

    public bool HasPassword => Password.Length > 0;

    public string PasswordText => !HasPassword ? "no password" : PasswordShown ? Password : "••••••••";

    public string PasswordToggleLabel => PasswordShown ? "Hide password" : "Show password";

    [ObservableProperty]
    public partial bool IsTeamVersus { get; set; }

    [RelayCommand]
    private void TogglePassword() => PasswordShown = !PasswordShown;

    [RelayCommand]
    private Task CopyPassword() => Main.Services.Shell.CopyAsync(Password);

    // room settings flyout
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int EditTypeIndex { get; set; }

    [ObservableProperty]
    public partial int EditQueueIndex { get; set; }

    [ObservableProperty]
    public partial double EditSize { get; set; } = 16;

    public void ResetSettingsForm()
    {
        var s = Room.State;
        EditName = s.Name;
        EditPassword = s.Password;
        EditTypeIndex = s.MatchType == MatchType.TeamVersus ? 0 : 1;
        EditQueueIndex = (int)s.QueueMode;
        EditSize = s.MaxParticipants ?? 16;
    }

    [RelayCommand]
    private Task ApplySettings() => run(() => Room.ChangeSettingsAsync(new ChangeRoomSettingsRequest
    {
        Name = EditName.Trim() != Room.State.Name ? EditName.Trim() : null,
        Password = EditPassword != Room.State.Password ? EditPassword : null,
        MatchType = EditTypeIndex == 0 ? MatchType.TeamVersus : MatchType.HeadToHead,
        QueueMode = (QueueMode)EditQueueIndex,
        MaxParticipants = (byte)EditSize,
    }));

    [RelayCommand]
    private Task CloseRoom() => run(() => Room.CloseAsync());

    // ---- countdown ----

    [ObservableProperty]
    public partial bool Counting { get; set; }

    [ObservableProperty]
    public partial string CountdownClock { get; set; } = string.Empty;

    [RelayCommand]
    private Task StopCountdown() => run(() => Room.StopCountdownAsync());

    public void Tick()
    {
        if (Room.State.MatchStartCountdown is { } cd)
            CountdownClock = Ui.Clock(cd.EndsAt - DateTimeOffset.UtcNow);
    }

    // ---- current map ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentMap))]
    public partial MapCardViewModel? CurrentMap { get; set; }

    public bool HasCurrentMap => CurrentMap != null;

    // right after joining the current item is guessed until the room confirms it.
    [ObservableProperty]
    public partial bool CurrentGuess { get; set; } = true;

    [ObservableProperty]
    public partial int ReadyCount { get; set; }

    [ObservableProperty]
    public partial int PlayerCount { get; set; }

    [ObservableProperty]
    public partial string EditBeatmapId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditMods { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditAllowedMods { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EditFreestyle { get; set; }

    public void ResetItemForm()
    {
        var item = Room.State.CurrentPlaylistItem;
        EditBeatmapId = item?.BeatmapId.ToString() ?? string.Empty;
        EditMods = item == null ? string.Empty : Ui.ModsText(item.RequiredMods);
        EditAllowedMods = item == null ? string.Empty : Ui.ModsText(item.AllowedMods);
        EditFreestyle = item?.Freestyle ?? false;
    }

    [RelayCommand]
    private Task ApplyItem() => run(() =>
    {
        int? beatmap = int.TryParse(EditBeatmapId.Trim(), out int id) ? id : null;
        return Room.EditCurrentItemAsync(new EditCurrentPlaylistItemRequest
        {
            BeatmapId = beatmap != Room.State.CurrentPlaylistItem?.BeatmapId ? beatmap : null,
            RequiredMods = parseMods(EditMods),
            AllowedMods = parseMods(EditAllowedMods),
            Freestyle = EditFreestyle,
        });
    });

    private static Mod[] parseMods(string text)
        => text.Split([',', ' ', '+'], StringSplitOptions.RemoveEmptyEntries)
               .Where(m => !m.Equals("NM", StringComparison.OrdinalIgnoreCase))
               .Select(m => new Mod(m.ToUpperInvariant()))
               .ToArray();

    // ---- scoreboard (kept by the ref; the library doesn't decide map winners) ----

    [ObservableProperty]
    public partial string RedName { get; set; } = "RED";

    [ObservableProperty]
    public partial string BlueName { get; set; } = "BLUE";

    [ObservableProperty]
    public partial int ScoreRed { get; set; }

    [ObservableProperty]
    public partial int ScoreBlue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BestOfText))]
    public partial int BestOf { get; set; } = 9;

    public string BestOfText => $"BO{BestOf}";

    public ObservableCollection<IBrush> Pips { get; } = new ObservableCollection<IBrush>();

    public string PipLabel => $"Best of {BestOf}. {RedName} {ScoreRed}, {BlueName} {ScoreBlue}.";

    public string MatchScoreText => $"{ScoreRed} – {ScoreBlue}";

    private int firstTo => (BestOf + 1) / 2;

    [RelayCommand]
    private void RedPlus() => ScoreRed = Math.Min(firstTo, ScoreRed + 1);

    [RelayCommand]
    private void RedMinus() => ScoreRed = Math.Max(0, ScoreRed - 1);

    [RelayCommand]
    private void BluePlus() => ScoreBlue = Math.Min(firstTo, ScoreBlue + 1);

    [RelayCommand]
    private void BlueMinus() => ScoreBlue = Math.Max(0, ScoreBlue - 1);

    [RelayCommand]
    private void CycleBestOf()
    {
        int i = Array.IndexOf(best_of_options, BestOf);
        BestOf = best_of_options[(i + 1) % best_of_options.Length];
        ScoreRed = Math.Min(ScoreRed, firstTo);
        ScoreBlue = Math.Min(ScoreBlue, firstTo);
    }

    partial void OnScoreRedChanged(int value) => refreshPips();

    partial void OnScoreBlueChanged(int value) => refreshPips();

    partial void OnBestOfChanged(int value) => refreshPips();

    private void refreshPips()
    {
        Pips.Clear();
        for (int i = 0; i < BestOf; i++)
            Pips.Add(i < ScoreRed ? Ui.RED : i >= BestOf - ScoreBlue ? Ui.BLUE : Ui.Brush("#1f2824"));
        OnPropertyChanged(nameof(PipLabel));
        OnPropertyChanged(nameof(MatchScoreText));
    }

    // ---- timer ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimerBg), nameof(TimerFg))]
    public partial bool TimerRunning { get; set; }

    [ObservableProperty]
    public partial string TimerText { get; set; } = string.Empty;

    public IBrush TimerBg => TimerRunning ? Ui.Brush("#12302c") : Ui.Brush("#3a3150");

    public IBrush TimerFg => TimerRunning ? Ui.Brush("#8ff0e4") : Ui.Brush("#eef3f0");

    public string[] TimerPresets { get; }

    [RelayCommand]
    private void StartTimer(string preset)
    {
        try
        {
            var duration = new CommandArgs(preset, "timer <duration>").Duration(0);
            Room.StartTimer(duration, Main.Services.Settings.Timer);
            TimerRunning = true;
            TimerText = Ui.Clock(duration);
        }
        catch (Exception ex)
        {
            Main.Services.ReportError(ex);
        }
    }

    [RelayCommand]
    private void StopTimer() => Room.StopTimer();

    // ---- players ----

    public ObservableCollection<TeamGroupViewModel> Teams { get; } = new ObservableCollection<TeamGroupViewModel>();

    public ObservableCollection<RefChipViewModel> Referees { get; } = new ObservableCollection<RefChipViewModel>();

    [ObservableProperty]
    public partial string InviteText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RefereeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBans))]
    public partial string BannedText { get; set; } = string.Empty;

    public bool HasBans => BannedText.Length > 0;

    [ObservableProperty]
    public partial bool InviteFocusRequested { get; set; }

    [RelayCommand]
    private void FocusInvite() => InviteFocusRequested = !InviteFocusRequested;

    [RelayCommand]
    private async Task Invite()
    {
        string text = InviteText.Trim();
        if (text.Length == 0)
            return;

        if (await run(() => Room.InviteAsync(UserRef.Parse(text))))
            InviteText = string.Empty;
    }

    [RelayCommand]
    private async Task AddReferee()
    {
        string text = RefereeText.Trim();
        if (text.Length == 0)
            return;

        if (await run(() => Room.AddRefereeAsync(UserRef.Parse(text))))
            RefereeText = string.Empty;
    }

    internal Task<bool> PlayerAction(Func<Task> action) => run(action);

    // ---- playlist ----

    public ObservableCollection<PlaylistTileViewModel> Playlist { get; } = new ObservableCollection<PlaylistTileViewModel>();

    [ObservableProperty]
    public partial int PlayedCount { get; set; }

    [ObservableProperty]
    public partial string AddBeatmapId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AddMods { get; set; } = string.Empty;

    [RelayCommand]
    private async Task AddItem()
    {
        if (!int.TryParse(AddBeatmapId.Trim(), out int id))
        {
            Main.Services.ReportError("Beatmap ID must be a number.");
            return;
        }

        var current = Room.State.CurrentPlaylistItem;
        if (await run(() => Room.AddPlaylistItemAsync(new AddPlaylistItemRequest
            {
                BeatmapId = id,
                RulesetId = current?.RulesetId ?? 0,
                RequiredMods = parseMods(AddMods),
            })))
        {
            AddBeatmapId = string.Empty;
            AddMods = string.Empty;
        }
    }

    // ---- chat & activity ----

    public ObservableCollection<ChatLineViewModel> Chat { get; } = new ObservableCollection<ChatLineViewModel>();

    public ObservableCollection<ActivityViewModel> Activity { get; } = new ObservableCollection<ActivityViewModel>();

    public ObservableCollection<CommandHintViewModel> CommandHints { get; } = new ObservableCollection<CommandHintViewModel>();

    public string ChannelName => Room.State.ChatChannelId > 0 ? $"#mp_{RoomId}" : "Chat";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActivity), nameof(ChatTabFg), nameof(ActivityTabFg), nameof(ChatTabLine), nameof(ActivityTabLine))]
    public partial bool ShowChat { get; set; } = true;

    public bool ShowActivity => !ShowChat;

    public IBrush ChatTabFg => ShowChat ? Ui.Brush("#eef3f0") : Ui.Brush("#9fb0a8");

    public IBrush ActivityTabFg => ShowChat ? Ui.Brush("#9fb0a8") : Ui.Brush("#eef3f0");

    public IBrush ChatTabLine => ShowChat ? Accent : Brushes.Transparent;

    public IBrush ActivityTabLine => ShowChat ? Brushes.Transparent : Accent;

    [RelayCommand]
    private void SelectChat() => ShowChat = true;

    [RelayCommand]
    private void SelectActivity() => ShowChat = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommand), nameof(ChatFont))]
    public partial string ChatInput { get; set; } = string.Empty;

    public bool IsCommand => ChatInput.StartsWith('/');

    public bool ShowHints => IsCommand && CommandHints.Count > 0;

    public FontFamily ChatFont => IsCommand ? new FontFamily("JetBrains Mono, DejaVu Sans Mono, monospace") : FontFamily.Default;

    partial void OnChatInputChanged(string value)
    {
        CommandHints.Clear();
        if (value.StartsWith('/'))
        {
            string q = value[1..].Split(' ')[0];
            foreach (var d in Main.Services.Commands.Commands
                                  .Where(d => d.Names.Any(n => n.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
                                  .Take(5))
                CommandHints.Add(new CommandHintViewModel("/" + d.Usage, d.Summary, CommandHints.Count == 0));
        }

        OnPropertyChanged(nameof(ShowHints));
    }

    [RelayCommand]
    private async Task SendChat()
    {
        string text = ChatInput.Trim();
        if (text.Length == 0)
            return;

        ChatInput = string.Empty;

        if (text.StartsWith('/'))
        {
            commandContext.Session = session;
            commandContext.Room = Room;

            try
            {
                await Main.Services.Commands.ExecuteAsync(text, commandContext);
            }
            catch (Exception ex)
            {
                AddSystemLine(Ui.Describe(ex));
            }
            finally
            {
                // make/join/use in a tab switch that command context only; keep it on this room.
                commandContext.Room = Room;
            }

            return;
        }

        if (!await run(() => Room.SendChatAsync(text)))
            ChatInput = text;
    }

    [RelayCommand]
    private async Task SaveLog()
    {
        try
        {
            string log = Room.ExportLog(Main.Services.Settings.ChatLog);
            string? path = await Main.Services.Shell.SaveTextAsync($"mp_{RoomId}_{DateTime.Now:yyyyMMdd-HHmm}.txt", log);
            if (path != null)
                AddSystemLine($"Log saved to {path}");
        }
        catch (Exception ex)
        {
            Main.Services.ReportError(ex);
        }
    }

    public void AddSystemLine(string text) => Chat.Add(ChatLineViewModel.System(text));

    private void syncChat()
    {
        var fresh = Room.Transcript.Messages.Where(m => !seenMessages.Contains(m.MessageId)).OrderBy(m => m.MessageId).ToArray();
        foreach (var m in fresh)
        {
            seenMessages.Add(m.MessageId);
            if (m.Sender != null)
                session?.Users.Remember(m.Sender);

            var line = ChatLineViewModel.FromMessage(m, m.Sender?.Username ?? nameOf(m.SenderId), senderBrush(m.SenderId));
            if (pendingHighlights.Remove(m.MessageId, out var kinds))
                line.ApplyHighlight(kinds, Accent);
            Chat.Add(line);
        }
    }

    public void OnHighlight(Highlight h)
    {
        var line = Chat.FirstOrDefault(l => l.MessageId == h.Message.MessageId);
        if (line != null)
            line.ApplyHighlight(h.Kinds, Accent);
        else
            pendingHighlights[h.Message.MessageId] = h.Kinds;

        if (!IsActive)
            Badge++;
    }

    private IBrush senderBrush(int userId)
    {
        if (userId == session?.Self.Id)
            return Accent;
        if (Room.State.Players.TryGetValue(userId, out var p) && p.Team is MatchTeam t)
            return t == MatchTeam.Red ? Ui.Brush("#ff8a93") : Ui.Brush("#8ccaff");
        if (Room.State.Referees.Contains(userId))
            return Ui.Brush("#7fe3d4");
        return Ui.Brush("#eef3f0");
    }

    private string nameOf(long userId)
        => session?.Users.GetCachedUsername((int)userId) ?? $"#{userId}";

    private void onEvent(IRoomEvent e)
    {
        if (e is RoomSettingsChangedEvent)
            CurrentGuess = false;

        var a = ActivityViewModel.From(e, nameOf, senderBrush, this);
        if (a != null)
            Activity.Add(a);
    }

    // ---- results ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial ResultsViewModel? LatestResult { get; set; }

    public bool HasResult => LatestResult != null;

    private void onResult(MatchResult result)
    {
        var vm = new ResultsViewModel(this, result);
        LatestResult = vm;
        if (IsActive)
            Main.ShowResults(vm);
    }

    [RelayCommand]
    private void ShowResult()
    {
        if (LatestResult != null)
            Main.ShowResults(LatestResult);
    }

    internal ApiBeatmap? GetBeatmap(int id) => beatmaps.GetValueOrDefault(id);

    // ---- footer ----

    public CountdownOption[] Countdowns { get; }

    [ObservableProperty]
    public partial int SelectedCountdown { get; set; } = 30;

    partial void OnSelectedCountdownChanged(int value)
    {
        foreach (var c in Countdowns)
            c.Refresh();
        refreshStart();
    }

    [ObservableProperty]
    public partial bool Starting { get; set; }

    [ObservableProperty]
    public partial bool InProgress { get; set; }

    public bool CanStart => !Starting && !Counting && !InProgress;

    public bool CanAbort => InProgress;

    public string StartLabel => Starting ? "Waiting for room…"
        : InProgress ? "Match in progress"
        : Counting ? "Countdown running"
        : SelectedCountdown == 0 ? "Start match"
        : SelectedCountdown >= 60 ? $"Start in {SelectedCountdown / 60} min" : $"Start in {SelectedCountdown} s";

    public string StartIcon => Starting ? "spinner" : "play";

    public IBrush StartBg => CanStart ? Ui.Brush("#9be34a") : Ui.Brush("#2a3530");

    public IBrush StartFg => CanStart ? Ui.Brush("#10200a") : Ui.Brush("#9fb0a8");

    private void refreshStart()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanAbort));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(StartIcon));
        OnPropertyChanged(nameof(StartBg));
        OnPropertyChanged(nameof(StartFg));
    }

    partial void OnStartingChanged(bool value) => refreshStart();

    partial void OnCountingChanged(bool value) => refreshStart();

    partial void OnInProgressChanged(bool value) => refreshStart();

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart)
            return;

        Starting = true;
        await run(() => Room.StartAsync(SelectedCountdown == 0 ? null : SelectedCountdown));
        Starting = false;
    }

    [RelayCommand]
    private Task Abort() => run(() => Room.AbortAsync());

    [RelayCommand]
    private Task ToggleLock() => run(() => Room.SetLockedAsync(!Locked));

    [RelayCommand]
    private Task Roll() => run(() => Room.RollAsync());

    [RelayCommand]
    private void Back() => Main.ShowRooms();

    // ---- state projection ----

    private void apply(RoomSnapshot s)
    {
        Name = s.Name;
        var names = team_names.Match(s.Name);
        if (names.Success)
        {
            RedName = strip(names.Groups[1].Value).ToUpperInvariant();
            BlueName = strip(names.Groups[2].Value).ToUpperInvariant();
            TabTitle = $"{strip(names.Groups[1].Value)} vs {strip(names.Groups[2].Value)}";
        }
        else
        {
            RedName = "RED";
            BlueName = "BLUE";
            TabTitle = s.Name.Length > 28 ? s.Name[..26] + "…" : s.Name.Length > 0 ? s.Name : IdText;
        }

        IsTeamVersus = s.MatchType == MatchType.TeamVersus;
        TypeText = IsTeamVersus ? "TEAM VERSUS" : "HEAD TO HEAD";
        QueueText = s.QueueMode switch
        {
            QueueMode.AllPlayers => "ALL PLAYERS",
            QueueMode.AllPlayersRoundRobin => "ROUND ROBIN",
            _ => "HOST ONLY",
        };
        SizeText = s.MaxParticipants is byte m ? $"{m} MAX" : "NO LIMIT";
        Locked = s.Locked;
        Password = s.Password;

        Counting = s.MatchStartCountdown != null;
        InProgress = s.IsMatchInProgress;
        Tick();

        applyPlayers(s);
        applyPlaylist(s);
        refreshPips();

        BannedText = s.BannedUsers.Count == 0 ? string.Empty : $"{s.BannedUsers.Count} banned this session";
        OnPropertyChanged(nameof(ChannelName));
    }

    private static string strip(string team) => team.StartsWith("Team ", StringComparison.OrdinalIgnoreCase) ? team[5..].Trim() : team.Trim();

    private void applyPlayers(RoomSnapshot s)
    {
        var unknown = s.Players.Keys.Concat(s.Referees).Where(id => session?.Users.GetCached(id) == null && requestedUsers.Add(id)).ToArray();
        if (unknown.Length > 0 && session != null)
            _ = loadUsersAsync(unknown);

        Teams.Clear();
        var players = s.Players.Values.OrderBy(p => p.Slot ?? int.MaxValue).ThenBy(p => p.UserId).ToArray();
        ReadyCount = players.Count(p => p.Status == MatchUserStatus.Ready);
        PlayerCount = players.Length;

        if (IsTeamVersus)
        {
            add(RedName, Ui.RED, players.Where(p => p.Team == MatchTeam.Red));
            add(BlueName, Ui.BLUE, players.Where(p => p.Team == MatchTeam.Blue));
            var none = players.Where(p => p.Team == null).ToArray();
            if (none.Length > 0)
                add("NO TEAM YET", Ui.NEUTRAL, none);
        }
        else
        {
            add("PLAYERS", Ui.NEUTRAL, players);
        }

        Referees.Clear();
        int self = session?.Self.Id ?? 0;
        foreach (int id in s.Referees.OrderByDescending(id => id == self).ThenBy(id => id))
        {
            string name = session?.Users.GetCachedUsername(id) ?? $"#{id}";
            bool joined = s.JoinedReferees.Contains(id);
            Referees.Add(new RefChipViewModel(this, id, name, id == self, joined));
        }

        void add(string title, IBrush color, IEnumerable<PlayerState> members)
        {
            var rows = members.Select(p =>
            {
                var user = session?.Users.GetCached(p.UserId);
                return new PlayerRowViewModel(this, p, user?.Username ?? $"#{p.UserId}", user?.CountryCode ?? string.Empty);
            }).ToArray();
            Teams.Add(new TeamGroupViewModel(title, color, rows));
        }
    }

    private async Task loadUsersAsync(int[] ids)
    {
        try
        {
            await session!.Users.GetByIdsAsync(ids);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                apply(Room.State);
                foreach (var t in Main.PrivateChats)
                {
                    if (t.Name.StartsWith('#') && session?.Users.GetCachedUsername(t.PeerUserId) is { } n)
                        t.Name = n;
                }
            });
        }
        catch (Exception ex)
        {
            Main.Services.ReportError(ex);
        }
    }

    private void applyPlaylist(RoomSnapshot s)
    {
        var missing = s.Playlist.Select(p => p.BeatmapId).Where(id => !beatmaps.ContainsKey(id) && requestedBeatmaps.Add(id)).ToArray();
        if (missing.Length > 0 && session != null)
            _ = loadBeatmapsAsync(missing);

        Playlist.Clear();
        foreach (var item in s.Playlist.OrderBy(p => p.Order).ThenBy(p => p.PlaylistItemId))
        {
            bool current = item.PlaylistItemId == s.CurrentPlaylistItemId;
            Playlist.Add(new PlaylistTileViewModel(this, item, beatmaps.GetValueOrDefault(item.BeatmapId), current));
        }

        PlayedCount = s.Playlist.Count(p => p.WasPlayed);

        var cur = s.CurrentPlaylistItem;
        string? key = cur == null ? null : $"{cur.PlaylistItemId}/{cur.BeatmapId}/{cur.RulesetId}/{Ui.ModsText(cur.RequiredMods)}";
        if (key != currentKey)
        {
            currentKey = key;
            if (cur == null)
                CurrentMap = null;
            else
                _ = describeAsync(cur);
        }
    }

    private async Task loadBeatmapsAsync(int[] ids)
    {
        try
        {
            var found = await session!.Beatmaps.GetManyAsync(ids);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var (id, map) in found)
                    beatmaps[id] = map;
                applyPlaylist(Room.State);
            });
        }
        catch (Exception ex)
        {
            Main.Services.ReportError(ex);
        }
    }

    private async Task describeAsync(PlaylistItem item)
    {
        describe?.Cancel();
        var cts = describe = new CancellationTokenSource();

        try
        {
            var info = await Room.DescribeBeatmapAsync(item, cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested)
                    CurrentMap = new MapCardViewModel(item, info);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Main.Services.ReportError(ex);
        }
    }

    private async Task<bool> run(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Main.Services.ReportError(ex);
            return false;
        }
    }

    public void Dispose()
    {
        describe?.Cancel();
        foreach (var s in subscriptions)
            s.Dispose();
        subscriptions.Clear();
    }
}

public sealed record ModChip(string Code, IBrush Brush)
{
    public static ModChip[] From(IEnumerable<Mod> mods, bool freestyle = false)
    {
        var chips = mods.Select(m => new ModChip(m.Acronym, Ui.ModBrush(m.Acronym))).ToList();
        if (freestyle)
            chips.Add(new ModChip("FREE", Ui.ModBrush("FREE")));
        return chips.ToArray();
    }
}

public sealed record StatViewModel(string Label, string Value, string Original, IBrush Brush)
{
    public bool Changed => Original.Length > 0;
}

public sealed class MapCardViewModel
{
    public MapCardViewModel(PlaylistItem item, BeatmapInfo info)
    {
        var b = info.Beatmap;
        Order = item.Order;
        BeatmapId = item.BeatmapId;
        Title = b.Beatmapset?.Title ?? $"b/{b.Id}";
        Artist = b.Beatmapset?.Artist ?? string.Empty;
        Mapper = b.Beatmapset?.Creator ?? string.Empty;
        Difficulty = b.Version;
        Stars = info.StarRating.ToString("0.00");
        Mods = ModChip.From(item.RequiredMods, item.Freestyle);
        ModsText = Ui.ModsText(item.RequiredMods);

        var o = info.Original;
        var m = info.Modded;
        Stats =
        [
            stat("CS", m.CircleSize, o.CircleSize, v => v.ToString("0.##")),
            stat("AR", m.ApproachRate, o.ApproachRate, v => v.ToString("0.##")),
            stat("OD", m.OverallDifficulty, o.OverallDifficulty, v => v.ToString("0.##")),
            stat("BPM", m.Bpm, o.Bpm, v => v.ToString("0")),
            stat("LENGTH", m.TotalLength, o.TotalLength, Ui.Length),
        ];
    }

    private static StatViewModel stat(string label, double value, double original, Func<double, string> format)
    {
        bool changed = Math.Abs(value - original) > 0.001;
        return new StatViewModel(label, format(value), changed ? format(original) : string.Empty, changed ? Ui.Brush("#ff9a4a") : Ui.Brush("#eef3f0"));
    }

    public int Order { get; }

    public int BeatmapId { get; }

    public string Title { get; }

    public string Artist { get; }

    public string Mapper { get; }

    public string Difficulty { get; }

    public string Stars { get; }

    public ModChip[] Mods { get; }

    public string ModsText { get; }

    public StatViewModel[] Stats { get; }

    public string OrderText => $"ITEM {Order}";

    public string MapperText => $"mapped by {Mapper}";

    public string IdText => $"b/{BeatmapId}";

    public string ShortTitle => $"{Title} [{Difficulty}]";
}

public sealed class TeamGroupViewModel
{
    public TeamGroupViewModel(string name, IBrush color, PlayerRowViewModel[] players)
    {
        Name = name;
        Color = color;
        Players = players;
        int ready = players.Count(p => p.IsReady);
        ReadyLabel = $"{ready} of {players.Length} ready";
        ReadyDots = players.Select(p => p.IsReady ? Ui.Brush("#9be34a") : Ui.Brush("#2a3530")).ToArray();
    }

    public string Name { get; }

    public IBrush Color { get; }

    public PlayerRowViewModel[] Players { get; }

    public string ReadyLabel { get; }

    public IBrush[] ReadyDots { get; }

    public bool IsEmpty => Players.Length == 0;
}

public sealed partial class PlayerRowViewModel : ObservableObject
{
    private readonly RoomViewModel room;

    public PlayerRowViewModel(RoomViewModel room, PlayerState p, string name, string country)
    {
        this.room = room;
        UserId = p.UserId;
        Name = name;
        Country = country;
        Initials = Ui.Initials(name);
        Mods = ModChip.From(p.Mods);
        IsReady = p.Status == MatchUserStatus.Ready;
        (Status, StatusBg, StatusFg) = p.Status switch
        {
            MatchUserStatus.Ready => ("READY", "#9be34a", "#10200a"),
            MatchUserStatus.Playing => ("PLAYING", "#ffcc33", "#2a1d00"),
            MatchUserStatus.FinishedPlay => ("FINISHED", "#12302c", "#8ff0e4"),
            MatchUserStatus.Spectating => ("SPECTATING", "#1d3346", "#a9d6ff"),
            _ => ("IDLE", "#2a3530", "#c6d3cd"),
        };
    }

    public int UserId { get; }

    public string Name { get; }

    public string Country { get; }

    public string Initials { get; }

    public ModChip[] Mods { get; }

    public bool IsReady { get; }

    public string Status { get; }

    public string StatusBg { get; }

    public string StatusFg { get; }

    public IBrush StatusBrush => Ui.Brush(StatusBg);

    public IBrush StatusForeground => Ui.Brush(StatusFg);

    public bool CanMove => room.IsTeamVersus;

    public string ActionsLabel => $"Actions for {Name}";

    private UserRef user => UserRef.FromId(UserId);

    [RelayCommand]
    private Task MoveRed() => room.PlayerAction(() => room.Room.SetTeamAsync(user, MatchTeam.Red));

    [RelayCommand]
    private Task MoveBlue() => room.PlayerAction(() => room.Room.SetTeamAsync(user, MatchTeam.Blue));

    [RelayCommand]
    private Task Kick() => room.PlayerAction(() => room.Room.KickAsync(user));

    [RelayCommand]
    private Task Ban() => room.PlayerAction(() => room.Room.BanAsync(user));

    [RelayCommand]
    private Task MakeReferee() => room.PlayerAction(() => room.Room.AddRefereeAsync(user));

    [RelayCommand]
    private void Message()
    {
        room.Main.PmTarget = Name;
    }
}

public sealed partial class RefChipViewModel : ObservableObject
{
    private readonly RoomViewModel room;

    public RefChipViewModel(RoomViewModel room, int id, string name, bool self, bool joined)
    {
        this.room = room;
        UserId = id;
        Name = name;
        Initials = Ui.Initials(name);
        IsSelf = self;
        Joined = joined;
        Title = $"{name}{(self ? " (you)" : "")}, {(joined ? "in room" : "referee but not in room")}";
        Background = self ? Ui.Brush("#3a2030") : Ui.Brush("#2a3530");
        Ring = self ? room.Accent : Brushes.Transparent;
        Dot = joined ? Ui.Brush("#9be34a") : Ui.Brush("#6b7b74");
        Opacity = joined ? 1 : 0.55;
    }

    public int UserId { get; }

    public string Name { get; }

    public string Initials { get; }

    public bool IsSelf { get; }

    public bool CanRemove => !IsSelf;

    public bool Joined { get; }

    public string Title { get; }

    public IBrush Background { get; }

    public IBrush Ring { get; }

    public IBrush Dot { get; }

    public double Opacity { get; }

    [RelayCommand]
    private Task Remove() => room.PlayerAction(() => room.Room.RemoveRefereeAsync(UserRef.FromId(UserId)));
}

public sealed partial class PlaylistTileViewModel : ObservableObject
{
    private readonly RoomViewModel room;
    private readonly PlaylistItem item;

    public PlaylistTileViewModel(RoomViewModel room, PlaylistItem item, ApiBeatmap? map, bool current)
    {
        this.room = room;
        this.item = item;
        Order = item.Order;
        Title = map?.Beatmapset?.Title is { } t ? $"{t} [{map.Version}]" : $"b/{item.BeatmapId}";
        Stars = map == null ? "?" : map.DifficultyRating.ToString("0.00");
        Mods = ModChip.From(item.RequiredMods, item.Freestyle);
        IsCurrent = current;
        Played = item.WasPlayed;
        Tag = current ? "CURRENT" : item.WasPlayed ? "PLAYED" : string.Empty;
        TagBrush = current ? room.Accent : Ui.Brush("#9fb0a8");
        Border = current ? room.Accent : Ui.Brush("#222b27");
        Background = current ? Ui.Brush("#2a1a24") : Ui.Brush("#1a221e");
        Opacity = item.WasPlayed && !current ? 0.55 : 1;
        Tooltip = $"Item {Order}: {Title}{(item.WasPlayed ? ", played" : "")}{(current ? ", current" : "")}. Base ★ shown; mods may change it.";
    }

    public int Order { get; }

    public string Title { get; }

    public string Stars { get; }

    public string StarsText => $"★ {Stars}";

    public ModChip[] Mods { get; }

    public bool IsCurrent { get; }

    public bool Played { get; }

    public string Tag { get; }

    public IBrush TagBrush { get; }

    public IBrush Border { get; }

    public IBrush Background { get; }

    public double Opacity { get; }

    public string Tooltip { get; }

    public bool CanRemove => !item.WasPlayed && !IsCurrent;

    [RelayCommand]
    private Task Remove() => room.PlayerAction(() => room.Room.RemovePlaylistItemAsync(item.PlaylistItemId));
}

public sealed record CommandHintViewModel(string Usage, string Summary, bool First)
{
    public IBrush Background => First ? Ui.Brush("#1f2824") : Brushes.Transparent;
}

public sealed partial class CountdownOption : ObservableObject
{
    private readonly RoomViewModel room;

    public CountdownOption(RoomViewModel room, int seconds)
    {
        this.room = room;
        Seconds = seconds;
        Label = seconds == 0 ? "now" : seconds >= 60 ? $"{seconds / 60}m" : $"{seconds}s";
    }

    public int Seconds { get; }

    public string Label { get; }

    public bool Selected => room.SelectedCountdown == Seconds;

    public IBrush Background => Selected ? Ui.Brush("#eef3f0") : Brushes.Transparent;

    public IBrush Foreground => Selected ? Ui.Brush("#0d1210") : Ui.Brush("#c6d3cd");

    [RelayCommand]
    private void Pick() => room.SelectedCountdown = Seconds;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
    }
}

public sealed record ActivityViewModel(string Time, IBrush Color, string Who, IBrush WhoBrush, string Text, string Chip)
{
    private static readonly IBrush result = Ui.Brush("#9be34a");
    private static readonly IBrush map = Ui.Brush("#b39bff");
    private static readonly IBrush player = Ui.Brush("#7fe3d4");
    private static readonly IBrush warn = Ui.Brush("#ffcc33");
    private static readonly IBrush roomBrush = Ui.Brush("#c6d3cd");
    private static readonly IBrush plain = Ui.Brush("#eef3f0");

    public bool HasChip => Chip.Length > 0;

    public IBrush ChipBrush => Ui.ModBrush(Chip);

    public static ActivityViewModel? From(IRoomEvent e, Func<long, string> name, Func<int, IBrush> brush, RoomViewModel room)
    {
        string t = DateTime.Now.ToString("HH:mm");
        ActivityViewModel user(long id, IBrush kind, string text, string chip = "")
            => new ActivityViewModel(t, kind, name(id), brush((int)id), text, chip);
        ActivityViewModel sys(IBrush kind, string who, string text, string chip = "")
            => new ActivityViewModel(t, kind, who, plain, text, chip);

        return e switch
        {
            UserJoinedEvent j => user(j.UserId, player, "joined the room"),
            UserLeftEvent l => user(l.UserId, warn, "left the room"),
            UserKickedEvent k => user(k.KickedUserId, warn, $"was kicked by {name(k.KickingUserId)}"),
            UserBannedEvent b => user(b.BannedUserId, warn, $"was banned by {name(b.BanningUserId)}"),
            RefereeAddedEvent r => user(r.UserId, roomBrush, "added as referee"),
            RefereeRemovedEvent r => user(r.UserId, roomBrush, "removed as referee"),
            RoomSettingsChangedEvent => sys(roomBrush, "Room", "settings changed"),
            MatchStateChangedEvent m => sys(roomBrush, "Room", m.State.Locked ? "locked" : "unlocked"),
            PlaylistItemAddedEvent p => sys(map, "Playlist", $"item added: b/{p.PlaylistItem.BeatmapId}", firstMod(p.PlaylistItem.RequiredMods)),
            PlaylistItemChangedEvent p => sys(map, "Map", $"changed to {room.GetBeatmap(p.PlaylistItem.BeatmapId)?.Beatmapset?.Title ?? "b/" + p.PlaylistItem.BeatmapId}", firstMod(p.PlaylistItem.RequiredMods)),
            PlaylistItemRemovedEvent => sys(map, "Playlist", "item removed"),
            RollCompletedEvent r => user(r.UserId, player, $"rolled {r.Result} out of {r.Max}"),
            UserStatusChangedEvent s when s.Status is MatchUserStatus.Ready => user(s.UserId, player, "is ready"),
            UserModsChangedEvent m => user(m.UserId, player, m.UserMods.Length == 0 ? "cleared mods" : "switched mods to", firstMod(m.UserMods)),
            UserTeamChangedEvent tc => user(tc.UserId, player, tc.Team switch
            {
                MatchTeam.Red => $"joined {room.RedName.ToLowerInvariant()}",
                MatchTeam.Blue => $"joined {room.BlueName.ToLowerInvariant()}",
                _ => "left their team",
            }),
            CountdownStartedEvent c when c.Type == CountdownType.ServerShuttingDown => sys(warn, "Server", $"restarting in {Ui.Clock(TimeSpan.FromSeconds(c.Seconds))}"),
            CountdownStartedEvent c => sys(result, "Countdown", $"started: {Ui.Clock(TimeSpan.FromSeconds(c.Seconds))}"),
            CountdownStoppedEvent => sys(roomBrush, "Countdown", "stopped"),
            MatchStartedEvent => sys(result, "Match", "started"),
            MatchAbortedEvent => sys(warn, "Match", "aborted"),
            MatchCompletedEvent => sys(result, "Match", "finished, fetching scores"),
            _ => null,
        };
    }

    private static string firstMod(IEnumerable<Mod> mods) => mods.FirstOrDefault()?.Acronym ?? string.Empty;
}
