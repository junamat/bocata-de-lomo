using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefereeHubApi.Api;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Session;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class ResultsViewModel : ViewModelBase
{
    private static readonly Dictionary<string, string> grade_colors = new Dictionary<string, string>
    {
        ["XH"] = "#eef3f0", ["X"] = "#ffcc33", ["SH"] = "#eef3f0", ["S"] = "#ffcc33", ["A"] = "#9be34a", ["B"] = "#4fb1ff", ["C"] = "#b39bff", ["D"] = "#ff8a93", ["F"] = "#ff8a93",
    };

    private IReadOnlyList<MultiplayerScore> scores;

    public ResultsViewModel(RoomViewModel room, MatchResult result)
    {
        Room = room;
        Result = result;
        scores = result.Scores;
        Expected = result.ExpectedScoreCount;

        var item = room.Room.State.Playlist.FirstOrDefault(p => p.PlaylistItemId == result.PlaylistItemId);
        var map = item == null ? null : room.GetBeatmap(item.BeatmapId);
        ItemOrder = item?.Order ?? 0;
        Title = map?.Beatmapset?.Title ?? (item != null ? $"b/{item.BeatmapId}" : "Unknown map");
        Artist = map?.Beatmapset?.Artist ?? string.Empty;
        Difficulty = map?.Version ?? string.Empty;
        Mapper = map?.Beatmapset?.Creator ?? string.Empty;
        Stars = map == null ? "?" : map.DifficultyRating.ToString("0.00");
        Mods = item == null ? [] : ModChip.From(item.RequiredMods, item.Freestyle);
        build();
    }

    public RoomViewModel Room { get; }

    public MatchResult Result { get; }

    public int Expected { get; }

    public int ItemOrder { get; }

    public string MapText => ItemOrder > 0 ? $"Map {ItemOrder}" : "Map";

    public string ItemText => $"ITEM {ItemOrder}";

    public string Title { get; }

    public string Artist { get; }

    public string Difficulty { get; }

    public string Mapper { get; }

    public string MapperText => $"mapped by {Mapper}";

    public string Stars { get; }

    public ModChip[] Mods { get; }

    public string RedName => Room.RedName;

    public string BlueName => Room.BlueName;

    [ObservableProperty]
    public partial bool IsTeams { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Partial), nameof(CountText), nameof(PartialText))]
    public partial int Count { get; set; }

    public bool Partial => Count < Expected;

    public string CountText => $"{Count}/{Expected} SCORES";

    public string PartialText => $"{Count} of {Expected} scores";

    [ObservableProperty]
    public partial string WinnerName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WinnerScore { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush WinnerBrush { get; set; } = Ui.BLUE;

    [ObservableProperty]
    public partial IBrush WinnerPale { get; set; } = Ui.Brush("#d3ebff");

    [ObservableProperty]
    public partial string LoserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoserScore { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush LoserBrush { get; set; } = Ui.RED;

    [ObservableProperty]
    public partial IBrush LoserPale { get; set; } = Ui.Brush("#ffd6da");

    [ObservableProperty]
    public partial string Difference { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Tie { get; set; }

    [ObservableProperty]
    public partial string AccuracyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccuracyOther { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TopScore { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TopName { get; set; } = string.Empty;

    public string MatchScore => Room.MatchScoreText;

    public string BestOfText => Room.BestOfText;

    [ObservableProperty]
    public partial double Share { get; set; } = 0.5;

    [ObservableProperty]
    public partial string ShareText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ScoreRowViewModel[] Rows { get; set; } = [];

    private string summary = string.Empty;

    private void build()
    {
        Count = scores.Count;
        var teams = Result.Match?.Teams;
        MatchTeam? teamOf(int userId)
            => teams != null && teams.TryGetValue(userId, out var t) ? t
                : Room.Room.State.Players.TryGetValue(userId, out var p) ? p.Team : null;

        IsTeams = (Result.Match?.Type ?? Room.Room.State.MatchType) == MatchType.TeamVersus;
        var ordered = scores.OrderByDescending(s => s.TotalScore).ToArray();
        long top = Math.Max(1, ordered.FirstOrDefault()?.TotalScore ?? 1);
        string name(MultiplayerScore s) => s.User?.Username ?? Room.Main.Session?.Users.GetCachedUsername(s.UserId) ?? $"#{s.UserId}";

        Rows = ordered.Select((s, i) =>
        {
            var team = IsTeams ? teamOf(s.UserId) : null;
            string grade = s.Passed ? s.Rank ?? "?" : "F";
            return new ScoreRowViewModel(
                $"#{i + 1}", name(s), Ui.Initials(name(s)),
                team == MatchTeam.Red ? Ui.RED : team == MatchTeam.Blue ? Ui.BLUE : Ui.NEUTRAL,
                (double)s.TotalScore / top, grade, Ui.Brush(grade_colors.GetValueOrDefault(grade, "#c6d3cd")),
                s.TotalScore.ToString("N0"), s.Accuracy.ToString("P2"), $"{s.MaxCombo:N0}x",
                s.Mods.Select(m => new ModChip(m.Acronym, Ui.ModBrush(m.Acronym))).ToArray(), !s.Passed);
        }).ToArray();

        var best = ordered.FirstOrDefault();
        TopScore = best?.TotalScore.ToString("N0") ?? "–";
        TopName = best == null ? string.Empty : name(best);

        if (IsTeams)
        {
            var red = scores.Where(s => teamOf(s.UserId) == MatchTeam.Red).ToArray();
            var blue = scores.Where(s => teamOf(s.UserId) == MatchTeam.Blue).ToArray();
            long r = red.Sum(s => s.TotalScore), b = blue.Sum(s => s.TotalScore);
            bool blueWins = b > r;
            Tie = r == b;
            (WinnerName, WinnerScore, WinnerBrush, WinnerPale) = blueWins ? (BlueName, b.ToString("N0"), Ui.BLUE, Ui.Brush("#d3ebff")) : (RedName, r.ToString("N0"), Ui.RED, Ui.Brush("#ffd6da"));
            (LoserName, LoserScore, LoserBrush, LoserPale) = blueWins ? (RedName, r.ToString("N0"), Ui.RED, Ui.Brush("#ffd6da")) : (BlueName, b.ToString("N0"), Ui.BLUE, Ui.Brush("#d3ebff"));
            Difference = $"−{Math.Abs(r - b):N0}";
            var wAcc = blueWins ? blue : red;
            var lAcc = blueWins ? red : blue;
            AccuracyText = wAcc.Length == 0 ? "–" : wAcc.Average(s => s.Accuracy).ToString("P2");
            AccuracyOther = lAcc.Length == 0 ? string.Empty : $"/ {lAcc.Average(s => s.Accuracy):P2}";
            Share = r + b == 0 ? 0.5 : (double)Math.Max(r, b) / (r + b);
            ShareText = Share.ToString("P1");
            summary = Tie
                ? $"{MapText}: tie, {RedName} {r:N0} - {BlueName} {b:N0}"
                : $"{MapText}: {WinnerName} wins, {WinnerScore} to {LoserScore} ({Math.Abs(r - b):N0})";
        }
        else
        {
            var second = ordered.Skip(1).FirstOrDefault();
            WinnerName = TopName.ToUpperInvariant();
            WinnerScore = TopScore;
            WinnerBrush = Ui.Brush("#ffcc33");
            WinnerPale = Ui.Brush("#eef3f0");
            LoserName = second == null ? string.Empty : name(second).ToUpperInvariant();
            LoserScore = second?.TotalScore.ToString("N0") ?? string.Empty;
            LoserBrush = Ui.NEUTRAL;
            LoserPale = Ui.Brush("#c6d3cd");
            Difference = second == null ? string.Empty : $"−{best!.TotalScore - second.TotalScore:N0}";
            AccuracyText = scores.Count == 0 ? "–" : scores.Average(s => s.Accuracy).ToString("P2");
            AccuracyOther = string.Empty;
            long all = scores.Sum(s => s.TotalScore);
            Share = all == 0 ? 0 : (double)(best?.TotalScore ?? 0) / all;
            ShareText = Share.ToString("P1");
            summary = best == null ? $"{MapText}: no scores" : $"{MapText}: {TopName} wins with {TopScore}";
        }

        OnPropertyChanged(nameof(MatchScore));
    }

    [RelayCommand]
    private async Task Refetch()
    {
        try
        {
            scores = await Room.Room.FetchScoresAsync(Result.PlaylistItemId);
            build();
        }
        catch (Exception ex)
        {
            Room.Main.Services.ReportError(ex);
        }
    }

    [RelayCommand]
    private async Task PostToChat()
    {
        try
        {
            await Room.Room.SendChatAsync(summary);
        }
        catch (Exception ex)
        {
            Room.Main.Services.ReportError(ex);
        }
    }

    [RelayCommand]
    private Task SaveLog() => Room.SaveLogCommand.ExecuteAsync(null);

    [RelayCommand]
    private void Back() => Room.Main.ShowRoom(Room);
}

public sealed record ScoreRowViewModel(
    string Rank, string Name, string Initials, IBrush TeamBrush, double Bar, string Grade, IBrush GradeBrush,
    string Score, string Accuracy, string Combo, ModChip[] Mods, bool Failed)
{
    public double Opacity => Failed ? 0.55 : 1;
}
