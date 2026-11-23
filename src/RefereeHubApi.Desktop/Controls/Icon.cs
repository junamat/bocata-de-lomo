using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RefereeHubApi.Desktop.Controls;

// stroke icons drawn on a 24x24 grid.
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2);

    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

    private static readonly Dictionary<string, string> paths = new Dictionary<string, string>
    {
        ["settings"] = "M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0Z M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1",
        ["home"] = "M3 10 12 3 21 10v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z",
        ["plus"] = "M12 5v14M5 12h14",
        ["chat"] = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2Z",
        ["warn"] = "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z M12 9v4M12 17h.01",
        ["key"] = "M12 15.5a4.5 4.5 0 1 1-9 0a4.5 4.5 0 1 1 9 0Z M10.7 12.3 20 3M17 6l3 3M14 9l2 2",
        ["eye"] = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0Z",
        ["copy"] = "M11 9h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-8a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2Z M5 15V5a2 2 0 0 1 2-2h10",
        ["sliders"] = "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12 M18 6a2 2 0 1 1-4 0a2 2 0 1 1 4 0Z M12 12a2 2 0 1 1-4 0a2 2 0 1 1 4 0Z M20 18a2 2 0 1 1-4 0a2 2 0 1 1 4 0Z",
        ["close"] = "M18 6 6 18M6 6l12 12",
        ["more"] = "M13 5a1 1 0 1 1-2 0a1 1 0 1 1 2 0Z M13 12a1 1 0 1 1-2 0a1 1 0 1 1 2 0Z M13 19a1 1 0 1 1-2 0a1 1 0 1 1 2 0Z",
        ["shield"] = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z",
        ["ban"] = "M21 12a9 9 0 1 1-18 0a9 9 0 1 1 18 0Z M5.6 5.6l12.8 12.8",
        ["download"] = "M12 3v12 M7 10l5 5 5-5 M5 21h14",
        ["back"] = "M22 12a10 10 0 1 1-20 0a10 10 0 1 1 20 0Z M12 8l-4 4 4 4 M16 12H8",
        ["userplus"] = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M13 7a4 4 0 1 1-8 0a4 4 0 1 1 8 0Z M19 8v6 M22 11h-6",
        ["dice"] = "M6 3h12a3 3 0 0 1 3 3v12a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3Z M9.5 8.5a1 1 0 1 1-2 0a1 1 0 1 1 2 0Z M16.5 15.5a1 1 0 1 1-2 0a1 1 0 1 1 2 0Z M13 12a1 1 0 1 1-2 0a1 1 0 1 1 2 0Z",
        ["lock"] = "M6 11h12a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2Z M8 11V7a4 4 0 0 1 8 0v4",
        ["unlock"] = "M6 11h12a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2Z M8 11V7a4 4 0 0 1 7.8-1.2",
        ["timer"] = "M20 13a8 8 0 1 1-16 0a8 8 0 1 1 16 0Z M12 9v4l2 2 M10 2h4",
        ["play"] = "M7 4v16l13-8Z",
        ["spinner"] = "M12 4a8 8 0 1 0 8 8h-3a5 5 0 1 1-5-5Z",
        ["users"] = "M13 8a4 4 0 1 1-8 0a4 4 0 1 1 8 0Z M2 21v-1a6 6 0 0 1 12 0v1 M16 4a4 4 0 0 1 0 8 M22 21v-1a6 6 0 0 0-4-5.6",
        ["history"] = "M3 12a9 9 0 1 0 3-6.7L3 8 M3 3v5h5 M12 7v5l3 2",
        ["bars"] = "M4 20V10M10 20V4M16 20v-7M22 20H2",
        ["browser"] = "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z M2 9h20",
        ["check"] = "M5 12l5 5 9-10",
        ["trophy"] = "M8 21h8M12 17v4M7 4h10v5a5 5 0 0 1-10 0Z M7 6H4a3 3 0 0 0 3 4M17 6h3a3 3 0 0 1-3 4",
    };

    private static readonly Dictionary<string, Geometry> cache = new Dictionary<string, Geometry>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeWidthProperty, FilledProperty);
    }

    public Icon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Kind == null || Foreground == null || !paths.TryGetValue(Kind, out string? data))
            return;

        if (!cache.TryGetValue(Kind, out var geometry))
            cache[Kind] = geometry = Geometry.Parse(data);

        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24;

        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (Filled)
                context.DrawGeometry(Foreground, null, geometry);
            else
                context.DrawGeometry(null, new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
