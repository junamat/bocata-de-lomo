using Avalonia.Media;
using Avalonia.Threading;
using RefereeHubApi.Auth;
using RefereeHubApi.Commands;
using RefereeHubApi.Hub;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Users;

namespace RefereeHubApi.Desktop.Services;

public static class Ui
{
    // library observables fire on background threads; view models only change on the UI thread.
    public static IDisposable OnUi<T>(this IObservable<T> source, Action<T> action)
        => source.Subscribe(new UiObserver<T>(action));

    private sealed class UiObserver<T>(Action<T> action) : IObserver<T>
    {
        public void OnNext(T value) => Dispatcher.UIThread.Post(() => action(value));

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static readonly IBrush RED = Brush("#ff5a6a");
    public static readonly IBrush BLUE = Brush("#4fb1ff");
    public static readonly IBrush NEUTRAL = Brush("#9fb0a8");

    private static readonly Dictionary<string, IBrush> mod_brushes = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase)
    {
        ["HD"] = Brush("#ffcc33"),
        ["HR"] = Brush("#ff5a6a"),
        ["DT"] = Brush("#b39bff"),
        ["NC"] = Brush("#b39bff"),
        ["HT"] = Brush("#7fe3d4"),
        ["DC"] = Brush("#7fe3d4"),
        ["EZ"] = Brush("#9be34a"),
        ["FL"] = Brush("#eef3f0"),
        ["FREE"] = Brush("#9be34a"),
    };

    private static readonly IBrush mod_default = Brush("#c6d3cd");

    public static IBrush ModBrush(string acronym) => mod_brushes.GetValueOrDefault(acronym, mod_default);

    public static string Initials(string name)
    {
        string letters = new string(name.Where(char.IsLetter).Take(2).ToArray());
        return letters.Length > 0 ? letters.ToUpperInvariant() : name.Length > 0 ? name[..Math.Min(2, name.Length)] : "?";
    }

    public static string Length(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string Clock(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        return $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    public static string Describe(Exception ex) => ex switch
    {
        RefereeHubException hub => hub.IsOpaque ? $"Hub error: {hub.HubMessage}" : $"Hub error {hub.RawCode} ({hub.Code}): {hub.HubMessage}",
        CommandException cmd => cmd.Message,
        UserNotFoundException user => user.Message,
        ReauthenticationRequiredException => "Your osu! login expired. Sign in again.",
        OAuthException oauth => $"osu! login failed: {oauth.Message}",
        HttpRequestException http => $"Network error: {http.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    public static string ModsText(IEnumerable<Mod> mods) => string.Join(",", mods.Select(m => m.Acronym));
}
