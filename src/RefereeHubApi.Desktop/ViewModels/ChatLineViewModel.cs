using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RefereeHubApi.Api;
using RefereeHubApi.Desktop.Services;
using RefereeHubApi.Highlights;

namespace RefereeHubApi.Desktop.ViewModels;

public sealed partial class ChatLineViewModel : ObservableObject
{
    private static readonly IBrush highlight_bg = Ui.Brush("#1fff66ab");
    private static readonly IBrush system_brush = Ui.Brush("#9fb0a8");

    public long MessageId { get; init; }

    public required string Time { get; init; }

    public required string Who { get; init; }

    public required IBrush WhoBrush { get; init; }

    public required string Text { get; init; }

    public FontStyle Style { get; init; } = FontStyle.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTag))]
    public partial string Tag { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush TagBrush { get; set; } = Brushes.Transparent;

    [ObservableProperty]
    public partial IBrush Background { get; set; } = Brushes.Transparent;

    public bool HasTag => Tag.Length > 0;

    public static ChatLineViewModel FromMessage(ChatMessage m, string who, IBrush whoBrush) => new ChatLineViewModel
    {
        MessageId = m.MessageId,
        Time = m.Timestamp.ToLocalTime().ToString("HH:mm"),
        Who = who,
        WhoBrush = whoBrush,
        Text = m.Content,
        Style = m.IsAction ? FontStyle.Italic : FontStyle.Normal,
    };

    public static ChatLineViewModel System(string text) => new ChatLineViewModel
    {
        Time = DateTime.Now.ToString("HH:mm"),
        Who = "*",
        WhoBrush = system_brush,
        Text = text,
        Style = FontStyle.Italic,
    };

    public void ApplyHighlight(HighlightKind kinds, IBrush accent)
    {
        if (kinds.HasFlag(HighlightKind.Mention))
        {
            Tag = "MENTION";
            TagBrush = accent;
        }
        else if (kinds.HasFlag(HighlightKind.RefereeCall))
        {
            Tag = "REF CALL";
            TagBrush = Ui.Brush("#ffcc33");
        }
        else if (kinds.HasFlag(HighlightKind.Keyword))
        {
            Tag = "KEYWORD";
            TagBrush = Ui.Brush("#ffcc33");
        }
        else
        {
            return;
        }

        Background = highlight_bg;
    }
}
