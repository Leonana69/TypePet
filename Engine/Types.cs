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
/// Who is driving the pet. <see cref="Autonomous"/> is the default — the pet roams on its own.
/// <see cref="Manual"/> means an external controller (e.g. an LLM via the control API) has taken
/// over: autonomous roaming is frozen until control is released.
/// </summary>
public enum ControlMode { Autonomous, Manual }

/// <summary>
/// The raw geometry captured from the OS: the visible window rectangles plus the
/// taskbar and the edge it is docked to. Produced by an <c>IWindowTracker</c>.
/// </summary>
public sealed record WorldGeometry(
    IReadOnlyList<Rect> Windows,
    Rect Taskbar,
    TaskbarEdge TaskbarEdge)
{
    /// <summary>
    /// Fixed per-display floors, in the same units as <see cref="Windows"/>. Each is a thin strip at
    /// a display's bottom edge (the pet walks on its top face). Unlike <see cref="Taskbar"/> there can
    /// be one per display: every screen gets a floor so the pet always has somewhere to stand, even a
    /// secondary display with no Dock/taskbar and no windows. Always emitted as walkable platforms by
    /// <c>WorldModel</c> (never occluded). The Dock/taskbar's own display is covered by
    /// <see cref="Taskbar"/> instead, so it is not duplicated here.
    /// </summary>
    public IReadOnlyList<Rect> Grounds { get; init; } = System.Array.Empty<Rect>();
}
