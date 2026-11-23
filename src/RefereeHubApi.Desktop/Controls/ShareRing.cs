using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RefereeHubApi.Desktop.Controls;

// two-segment ring for the results score share.
public sealed class ShareRing : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<ShareRing, double>(nameof(Fraction), 0.5);

    public static readonly StyledProperty<IBrush?> FirstProperty = AvaloniaProperty.Register<ShareRing, IBrush?>(nameof(First));

    public static readonly StyledProperty<IBrush?> SecondProperty = AvaloniaProperty.Register<ShareRing, IBrush?>(nameof(Second));

    static ShareRing()
    {
        AffectsRender<ShareRing>(FractionProperty, FirstProperty, SecondProperty);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? First
    {
        get => GetValue(FirstProperty);
        set => SetValue(FirstProperty, value);
    }

    public IBrush? Second
    {
        get => GetValue(SecondProperty);
        set => SetValue(SecondProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double r = size / 2 - 14;
        const double gap = 0.008;

        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#1a221e")), 22), c, r, r);
        arc(context, c, r, 0, Fraction - gap, First);
        arc(context, c, r, Fraction + gap, 1 - gap, Second);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#997fe3d4")), 4), c, r - 32, r - 32);
    }

    private static void arc(DrawingContext ctx, Point c, double r, double from, double to, IBrush? brush)
    {
        if (brush == null || to <= from)
            return;

        double a0 = from * Math.Tau - Math.PI / 2, a1 = to * Math.Tau - Math.PI / 2;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
            g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, to - from > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        ctx.DrawGeometry(null, new Pen(brush, 22), geo);
    }
}
