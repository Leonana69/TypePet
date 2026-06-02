namespace MaplePet.Engine;

/// <summary>A 2D vector / point in logical (device-independent) pixels.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
}

/// <summary>An axis-aligned rectangle. Used both for raw screen geometry and the world model.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Top => Y;
    public double Left => X;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Which screen edge the taskbar is docked to.</summary>
public enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>
/// The raw geometry captured from the OS: the visible window rectangles plus the
/// taskbar and the edge it is docked to. Produced by an <c>IWindowTracker</c>.
/// </summary>
public sealed record WorldGeometry(
    IReadOnlyList<Rect> Windows,
    Rect Taskbar,
    TaskbarEdge TaskbarEdge);
