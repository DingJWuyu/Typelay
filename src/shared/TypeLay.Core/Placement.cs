namespace TypeLay.Core;

// Geometry is in physical pixels, including negative monitor coordinates.
public readonly record struct RectPx(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
        && double.IsFinite(Height) && Width > 0 && Height > 0;
    public bool Intersects(RectPx other) => X < other.Right && Right > other.X
        && Y < other.Bottom && Bottom > other.Y;
    public bool Contains(RectPx other) => other.X >= X && other.Y >= Y
        && other.Right <= Right && other.Bottom <= Bottom;
}

public static class OverlayPlacement
{
    // Above first, below only if necessary. Shrink within available vertical space.
    // Never clamp a panel on top of the input merely to make it visible.
    public static RectPx? Place(RectPx input, RectPx work, double desiredWidth,
        double desiredHeight, double gap = 8, double minimumHeight = 72)
    {
        if (!input.IsValid || !work.IsValid || !input.Intersects(work)
            || !double.IsFinite(desiredWidth) || !double.IsFinite(desiredHeight)
            || !double.IsFinite(gap) || !double.IsFinite(minimumHeight)
            || desiredWidth <= 0 || desiredHeight <= 0 || gap < 0 || minimumHeight <= 0)
            return null;
        double width = Math.Min(desiredWidth, work.Width);
        double x = Math.Clamp(input.X, work.X, work.Right - width);
        double above = input.Y - gap - work.Y;
        double below = work.Bottom - input.Bottom - gap;
        double minimum = Math.Min(minimumHeight, desiredHeight);
        if (above >= desiredHeight)
            return new(x, input.Y - gap - desiredHeight, width, desiredHeight);
        if (below >= desiredHeight)
            return new(x, input.Bottom + gap, width, desiredHeight);
        if (above >= minimum)
            return new(x, work.Y, width, Math.Min(above, desiredHeight));
        if (below >= minimum)
            return new(x, input.Bottom + gap, width, Math.Min(below, desiredHeight));
        return null;
    }
}
