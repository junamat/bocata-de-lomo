using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RefereeHubApi.Desktop.Controls;

// the drifting translucent triangles behind each screen.
public sealed class TriangleField : Control
{
    public static readonly StyledProperty<int> SeedProperty = AvaloniaProperty.Register<TriangleField, int>(nameof(Seed), 27);

    public static readonly StyledProperty<double> BiasXProperty = AvaloniaProperty.Register<TriangleField, double>(nameof(BiasX), 0.7);

    private static readonly Color[] palette =
    [
        Color.Parse("#ff66ab"), Color.Parse("#4fb1ff"), Color.Parse("#7fe3d4"), Color.Parse("#ffcc33"), Color.Parse("#ff5a6a"),
    ];

    private readonly record struct Tri(double X, double Y, double Size, Color Color, double Alpha, double Period, double Phase);

    private Tri[] tris = [];
    private bool animating;
    private DateTime start = DateTime.UtcNow;

    public int Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    // where the cluster sits horizontally, 0 = left edge, 1 = right edge.
    public double BiasX
    {
        get => GetValue(BiasXProperty);
        set => SetValue(BiasXProperty, value);
    }

    public TriangleField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeedProperty || change.Property == BiasXProperty)
            build();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (tris.Length == 0)
            build();
        animating = true;
        requestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        animating = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void build()
    {
        var rng = new Random(Seed);
        tris = Enumerable.Range(0, 12).Select(i =>
        {
            bool cluster = i < 8;
            double x = cluster ? Math.Clamp(BiasX + (rng.NextDouble() - 0.5) * 0.45, 0, 1) : rng.NextDouble();
            return new Tri(
                x,
                rng.NextDouble(),
                cluster ? 60 + rng.NextDouble() * 160 : 40 + rng.NextDouble() * 100,
                palette[rng.Next(palette.Length)],
                0.06 + rng.NextDouble() * 0.07,
                28 + rng.NextDouble() * 18,
                rng.NextDouble());
        }).ToArray();
    }

    private void requestFrame()
    {
        if (!animating)
            return;

        // ~20 fps is plenty for a slow drift.
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            InvalidateVisual();
            requestFrame();
        }, TimeSpan.FromMilliseconds(50));
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        double t = (DateTime.UtcNow - start).TotalSeconds;

        foreach (var tri in tris)
        {
            double p = (t / tri.Period + tri.Phase) % 1;
            double fade = p < 0.15 ? p / 0.15 : p > 0.8 ? (1 - p) / 0.2 : 1;
            double dy = 90 - 350 * p;
            double cx = tri.X * w, cy = tri.Y * h + dy, s = tri.Size;

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(cx, cy - s * 0.577), true);
                g.LineTo(new Point(cx + s / 2, cy + s * 0.289));
                g.LineTo(new Point(cx - s / 2, cy + s * 0.289));
                g.EndFigure(true);
            }

            var c = tri.Color;
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(255 * tri.Alpha * fade), c.R, c.G, c.B)), null, geo);
        }
    }
}
