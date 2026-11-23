using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RefereeHubApi.Desktop.Controls;

// a decorator that paints a parallelogram leaning right, fitted inside its own bounds.
// unlike a SkewTransform, nothing is drawn outside the layout box, so parents never clip the corners,
// and the child stays upright without a counter-skew.
public sealed class Slant : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<Slant>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<Slant>();

    public static readonly StyledProperty<Thickness> BorderThicknessProperty =
        Border.BorderThicknessProperty.AddOwner<Slant>();

    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<Slant, double>(nameof(Angle), 12);

    static Slant()
    {
        AffectsRender<Slant>(BackgroundProperty, BorderBrushProperty, BorderThicknessProperty, AngleProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    // degrees the sides lean from vertical.
    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        // horizontal run of a slanted side, capped so very short-and-tall shapes don't invert.
        var run = Math.Min(h * Math.Tan(Angle * Math.PI / 180), w / 2);

        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(run, 0), true);
            g.LineTo(new Point(w, 0));
            g.LineTo(new Point(w - run, h));
            g.LineTo(new Point(0, h));
            g.EndFigure(true);
        }

        if (Background is { } bg)
            context.DrawGeometry(bg, null, shape);

        var t = BorderThickness.Left;
        if (BorderBrush is { } brush && t > 0)
        {
            // stroke centred on the outline at double width, clipped to the shape: an inside border of width t.
            using (context.PushGeometryClip(shape))
                context.DrawGeometry(null, new Pen(brush, t * 2), shape);
        }
    }
}
