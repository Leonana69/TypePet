using Avalonia.Media;
using MaplePet.Engine;

namespace MaplePet.Rendering;

/// <summary>
/// Visualizes what the window tracker detects (M2): every window rectangle, the taskbar,
/// and the derived platforms (green) and ladders (orange). Indispensable for confirming
/// the geometry tracks real windows snugly on their visible edges.
/// </summary>
public static class DebugOverlay
{
    private static readonly IPen WindowPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 200, 255)), 1);
    private static readonly IPen TaskbarPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 0, 200)), 2);
    private static readonly IPen PlatformPen = new Pen(new SolidColorBrush(Color.FromArgb(225, 80, 230, 120)), 3);
    private static readonly IPen LadderPen = new Pen(new SolidColorBrush(Color.FromArgb(205, 255, 170, 40)), 3);
    private static readonly IPen PathPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 240, 90)), 2);
    private static readonly IBrush NodeBrush = new SolidColorBrush(Color.FromArgb(220, 255, 240, 90));
    private static readonly IBrush TargetBrush = new SolidColorBrush(Color.FromArgb(230, 240, 70, 70));

    public static void Draw(DrawingContext ctx, WorldGeometry geo, World world)
    {
        foreach (var w in geo.Windows)
            ctx.DrawRectangle(null, WindowPen, new Avalonia.Rect(w.X, w.Y, w.Width, w.Height));

        var tb = geo.Taskbar;
        if (tb.Width > 0 && tb.Height > 0)
            ctx.DrawRectangle(null, TaskbarPen, new Avalonia.Rect(tb.X, tb.Y, tb.Width, tb.Height));

        foreach (var p in world.Platforms)
            ctx.DrawLine(PlatformPen, new Avalonia.Point(p.XStart, p.Y), new Avalonia.Point(p.XEnd, p.Y));

        foreach (var l in world.Ladders)
            ctx.DrawLine(LadderPen, new Avalonia.Point(l.X, l.YTop), new Avalonia.Point(l.X, l.YBottom));
    }

    /// <summary>Draw the pet's current planned path and its target marker.</summary>
    public static void DrawPath(DrawingContext ctx, PetController pet)
    {
        var path = pet.Path;
        if (pet.HasTarget)
            ctx.DrawEllipse(TargetBrush, null, new Avalonia.Point(pet.TargetPos.X, pet.TargetPos.Y), 5, 5);
        if (path is null || path.Count == 0) return;

        double px = pet.CenterX, py = pet.FeetY;
        foreach (var s in path)
        {
            ctx.DrawLine(PathPen, new Avalonia.Point(px, py), new Avalonia.Point(s.X, s.Y));
            ctx.DrawEllipse(NodeBrush, null, new Avalonia.Point(s.X, s.Y), 3, 3);
            px = s.X; py = s.Y;
        }
    }
}
