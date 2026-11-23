using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Hub.Models.Requests;
using RefereeHubApi.Journal;
using RefereeHubApi.Logs;
using RefereeHubApi.Session;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class RoomsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel main;
    private CancellationTokenSource? lookup;
    private IReadOnlyList<long> listed = [];

    public RoomsViewModel(MainWindowViewModel main)
    {
        this.main = main;
        Rulesets =
        [
            new RulesetOption(this, 0, "osu!"),
            new RulesetOption(this, 1, "taiko"),
            new RulesetOption(this, 2, "catch"),
            new RulesetOption(this, 3, "mania"),
        ];
        RoomName = string.Empty;
    }

    public int RoomLimit => 4;

    public ObservableCollection<RoomCardViewModel> Rooms { get; } = new ObservableCollection<RoomCardViewModel>();

    public ObservableCollection<JournalEntryViewModel> Journal { get; } = new ObservableCollection<JournalEntryViewModel>();

    public ObservableCollection<IBrush> LimitPips { get; } = new ObservableCollection<IBrush>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AtLimit), nameof(OpenLabel))]
    public partial int OpenCount { get; set; }

    public bool AtLimit => OpenCount >= RoomLimit;

    public string OpenLabel => $"{OpenCount} of {RoomLimit} rooms open";

    public bool HasJournal => Journal.Count > 0;

    public bool HasRooms => Rooms.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectError))]
    public partial string? ConnectError { get; set; }

    public bool HasConnectError => ConnectError != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInvite))]
    public partial long? InvitedRoomId { get; set; }

    public bool HasInvite => InvitedRoomId != null;

    // make room form
    [ObservableProperty]
    public partial string RoomName { get; set; }

    [ObservableProperty]
    public partial string BeatmapId { get; set; } = string.Empty;

    public RulesetOption[] Rulesets { get; }

    [ObservableProperty]
    public partial int RulesetId { get; set; }

    [ObservableProperty]
    public partial int TypeIndex { get; set; } = 0;

    [ObservableProperty]
    public partial int QueueIndex { get; set; } = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    public partial double Size { get; set; } = 8;

    public string SizeLabel => ((int)Size).ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial string? PreviewTitle { get; set; }

    [ObservableProperty]
    public partial string PreviewStars { get; set; } = string.Empty;

    public bool HasPreview => PreviewTitle != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel))]
    public partial bool Making { get; set; }

    public string MakeLabel => Making ? "Making room…" : "Make room";

    partial void OnRulesetIdChanged(int value)
    {
        foreach (var r in Rulesets)
            r.Refresh();
    }

    partial void OnBeatmapIdChanged(string value)
    {
        lookup?.Cancel();
        PreviewTitle = null;
        if (!int.TryParse(value.Trim(), out int id) || main.Session is not { } session)
            return;

        var cts = lookup = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                var map = await session.Beatmaps.GetAsync(id, cts.Token);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;
                    if (map == null)
                    {
                        PreviewTitle = "Beatmap not found";
                        PreviewStars = "?";
                        return;
                    }

                    PreviewTitle = $"{map.Beatmapset?.Artist} - {map.Beatmapset?.Title} [{map.Version}]";
                    PreviewStars = $"★ {map.DifficultyRating:0.00}";
                    RulesetId = map.RulesetId;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // the preview is only a hint; Make room reports real errors.
            }
        });
    }

    public void ConnectFailed(string message) => ConnectError = message;

    [RelayCommand]
    private Task Retry()
    {
        ConnectError = null;
        return main.RetryCommand.ExecuteAsync(null);
    }

    public async Task OnConnectedAsync()
    {
        ConnectError = null;
        Refresh();
        await refreshListedAsync();
        await LoadJournalAsync();
    }

    private async Task refreshListedAsync()
    {
        if (main.Session is not { } session)
            return;

        try
        {
            listed = await session.ListRoomsAsync();
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }

        Refresh();
    }

    public void OnInvited(long roomId) => InvitedRoomId = roomId;

    [RelayCommand]
    private void DismissInvite() => InvitedRoomId = null;

    [RelayCommand]
    private async Task JoinInvite()
    {
        if (InvitedRoomId is not long id || main.Session is not { } session)
            return;

        try
        {
            await session.JoinRoomAsync(id);
            InvitedRoomId = null;
            main.ShowRoom(id);
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }
    }

    public void Refresh()
    {
        Rooms.Clear();
        foreach (var r in main.RoomTabs)
            Rooms.Add(new RoomCardViewModel(main, r));

        OpenCount = Math.Max(Rooms.Count, listed.Count);
        LimitPips.Clear();
        for (int i = 0; i < RoomLimit; i++)
            LimitPips.Add(i < OpenCount ? (AtLimit ? Ui.Brush("#ff4d5e") : Ui.Brush("#ff66ab")) : Ui.Brush("#2a3530"));
        OnPropertyChanged(nameof(HasRooms));

        foreach (var j in Journal.ToArray())
        {
            if (main.Session?.GetRoom(j.Entry.RoomId) != null)
                Journal.Remove(j);
        }

        OnPropertyChanged(nameof(HasJournal));
    }

    public async Task LoadJournalAsync()
    {
        try
        {
            var entries = await main.Services.Journal.ListAsync();
            Journal.Clear();
            foreach (var e in entries.OrderByDescending(e => e.SavedAt))
            {
                if (main.Session?.GetRoom(e.RoomId) != null)
                    continue;
                Journal.Add(new JournalEntryViewModel(this, e, listed.Contains(e.RoomId)));
            }
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }

        OnPropertyChanged(nameof(HasJournal));
    }

    internal async Task RestoreAsync(JournalEntryViewModel j)
    {
        if (main.Session is not { } session)
            return;

        try
        {
            await session.JoinRoomAsync(j.Entry.RoomId);
            Journal.Remove(j);
            OnPropertyChanged(nameof(HasJournal));
            main.ShowRoom(j.Entry.RoomId);
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }
    }

    internal async Task DiscardAsync(JournalEntryViewModel j)
    {
        try
        {
            await main.Services.Journal.DeleteAsync(j.Entry.RoomId);
            Journal.Remove(j);
            OnPropertyChanged(nameof(HasJournal));
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }
    }

    internal async Task SaveLogAsync(JournalEntryViewModel j)
    {
        var e = j.Entry;
        var transcript = new ChatTranscript(0, e.Name, e.StartedAt);
        transcript.Seed(e.Chat, e.Results);
        string text = ChatLogFormatter.Format(transcript, main.Services.Settings.ChatLog, id => main.Session?.Users.GetCachedUsername(id));

        try
        {
            await main.Services.Shell.SaveTextAsync($"room-{e.RoomId}.txt", text);
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }
    }

    [RelayCommand]
    private async Task MakeRoom()
    {
        if (main.Session is not { } session || Making)
            return;

        if (!int.TryParse(BeatmapId.Trim(), out int beatmapId))
        {
            main.Services.ReportError("Enter a beatmap ID first: the hub needs a map to make a room.");
            return;
        }

        Making = true;

        try
        {
            var room = await session.MakeRoomAsync(new MakeRoomRequest
            {
                RoomName = RoomName.Trim(),
                BeatmapId = beatmapId,
                RulesetId = RulesetId,
                MatchType = TypeIndex == 0 ? MatchType.TeamVersus : MatchType.HeadToHead,
                QueueMode = (Hub.Models.QueueMode)QueueIndex,
                MaxParticipants = (byte)Size,
            });
            await refreshListedAsync();
            main.ShowRoom(room.RoomId);
        }
        catch (RoomLimitReachedException ex)
        {
            listed = ex.OpenRoomIds;
            Refresh();
            main.Services.ReportError($"Room limit reached: {ex.OpenRoomIds.Count} of {RoomLimit} rooms are open. Close one first.");
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }
        finally
        {
            Making = false;
        }
    }
}

public sealed partial class RulesetOption : ObservableObject
{
    private readonly RoomsViewModel owner;

    public RulesetOption(RoomsViewModel owner, int id, string name)
    {
        this.owner = owner;
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string Name { get; }

    public bool IsChecked => owner.RulesetId == Id;

    public IBrush Background => IsChecked ? Ui.Brush("#eef3f0") : Brushes.Transparent;

    public IBrush Foreground => IsChecked ? Ui.Brush("#0d1210") : Ui.Brush("#c6d3cd");

    [RelayCommand]
    private void Pick() => owner.RulesetId = Id;

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
    }
}

public sealed partial class RoomCardViewModel : ObservableObject
{
    private readonly MainWindowViewModel main;

    public RoomCardViewModel(MainWindowViewModel main, RoomViewModel room)
    {
        this.main = main;
        Room = room;
        var s = room.Room.State;
        Name = s.Name;
        Id = s.RoomId;
        Players = s.Players.Count;
        Max = s.MaxParticipants ?? 16;
        (StateText, StateBg, StateFg) = s.IsMatchInProgress ? ("PLAYING", "#ffcc33", "#2a1d00")
            : s.MatchStartCountdown != null ? ("STARTING", "#9be34a", "#10200a")
            : ("OPEN", "#2a3530", "#c6d3cd");
        MapText = room.CurrentMap?.ShortTitle;
        ModText = room.CurrentMap?.ModsText ?? string.Empty;
        TimerText = room.Room.Timer.IsRunning ? Ui.Clock(room.Room.Timer.Remaining) : null;
    }

    public RoomViewModel Room { get; }

    public string Name { get; }

    public long Id { get; }

    public int Players { get; }

    public int Max { get; }

    public string PlayersText => $"{Players}/{Max}";

    public string StateText { get; }

    public string StateBg { get; }

    public string StateFg { get; }

    public IBrush StateBrush => Ui.Brush(StateBg);

    public IBrush StateForeground => Ui.Brush(StateFg);

    public string? MapText { get; }

    public bool HasMap => MapText != null;

    public string ModText { get; }

    public bool HasMod => ModText.Length > 0;

    public string? TimerText { get; }

    public bool HasTimer => TimerText != null;

    [RelayCommand]
    private void Open() => main.ShowRoom(Room);

    [RelayCommand]
    private async Task Close()
    {
        try
        {
            await Room.Room.CloseAsync();
        }
        catch (Exception ex)
        {
            main.Services.ReportError(ex);
        }
    }
}

public sealed partial class JournalEntryViewModel : ObservableObject
{
    private readonly RoomsViewModel owner;

    public JournalEntryViewModel(RoomsViewModel owner, RoomJournalEntry entry, bool live)
    {
        this.owner = owner;
        Entry = entry;
        Live = live;
    }

    public RoomJournalEntry Entry { get; }

    public bool Live { get; }

    public bool Gone => !Live;

    public string Name => Entry.Name;

    public long Id => Entry.RoomId;

    public string ChatText => Entry.Chat.Count.ToString();

    public string ResultsText => Entry.Results.Count.ToString();

    public string SavedText => $"saved {Entry.SavedAt.ToLocalTime():g}";

    [RelayCommand]
    private Task Restore() => owner.RestoreAsync(this);

    [RelayCommand]
    private Task Discard() => owner.DiscardAsync(this);

    [RelayCommand]
    private Task SaveLog() => owner.SaveLogAsync(this);
}
