namespace TypePet.Engine;

/// <summary>A walkable, horizontal surface (a window top or bottom edge, or the taskbar face).</summary>
public readonly record struct Platform(double Y, double XStart, double XEnd)
{
    public double Width => XEnd - XStart;
    public double CenterX => (XStart + XEnd) / 2;
    public bool ContainsX(double x, double eps = 0.5) => x >= XStart - eps && x <= XEnd + eps;
}

/// <summary>A climbable, vertical surface (a window left or right edge). YTop &lt; YBottom.</summary>
public readonly record struct Ladder(double X, double YTop, double YBottom)
{
    public double Height => YBottom - YTop;
}

/// <summary>The collision world: a set of platforms and ladders, all in logical pixels.</summary>
public sealed record World(IReadOnlyList<Platform> Platforms, IReadOnlyList<Ladder> Ladders);

/// <summary>
/// Pure transformation from raw rectangles into the platform/ladder collision world.
/// No OS calls — fully unit-testable with synthetic geometry.
///
/// Only the VISIBLE part of each edge is emitted: a window's top/bottom/side edge is clipped
/// against every window in front of it (the input list is in Z-order, front-first) and against the
/// taskbar, so an edge split by overlapping windows produces several segments.
/// </summary>
public static class WorldModel
{
    private const double MinSegment = 4.0; // discard slivers shorter than this
    private const double Eps = 0.5;

    public static World Build(WorldGeometry g, Rect screen)
    {
        var platforms = new List<Platform>();
        var ladders = new List<Ladder>();

        var tb = g.Taskbar;
        bool hasTaskbar = tb.Width > 0 && tb.Height > 0;
        if (hasTaskbar)
        {
            double baseY = g.TaskbarEdge switch
            {
                TaskbarEdge.Top => tb.Bottom,    // docked top -> walk on its lower face
                TaskbarEdge.Bottom => tb.Top,    // docked bottom -> walk on its top face
                _ => tb.Top,                     // docked left/right -> stand on its top edge (v1)
            };
            platforms.Add(new Platform(baseY, tb.Left, tb.Right)); // ground; assumed always on top
        }

        // Per-display floors (one per screen): always-present walkable surfaces, never occluded — so a
        // pet confined to a secondary display with no windows still has a place to stand. Like the
        // taskbar, the pet walks on the top face and the strip occludes window edges within its band.
        var grounds = g.Grounds;
        foreach (var gr in grounds)
            if (gr.Width > 0 && gr.Height > 0)
                platforms.Add(new Platform(gr.Top, gr.Left, gr.Right));

        var wins = g.Windows; // Z-order: index 0 = frontmost
        for (int i = 0; i < wins.Count; i++)
        {
            var w = wins[i];
            if (w.Width <= 1 || w.Height <= 1) continue;

            var topHoles = new List<(double, double)>();
            var bottomHoles = new List<(double, double)>();
            var leftHoles = new List<(double, double)>();
            var rightHoles = new List<(double, double)>();

            // The taskbar sits in front of windows, hiding any window edge that falls within its band.
            if (hasTaskbar)
            {
                if (Covers(tb.Top, tb.Bottom, w.Top)) topHoles.Add((tb.Left, tb.Right));
                if (Covers(tb.Top, tb.Bottom, w.Bottom)) bottomHoles.Add((tb.Left, tb.Right));
                if (Covers(tb.Left, tb.Right, w.Left)) leftHoles.Add((tb.Top, tb.Bottom));
                if (Covers(tb.Left, tb.Right, w.Right)) rightHoles.Add((tb.Top, tb.Bottom));
            }

            // Per-display floors hide window edges within their band too (same as the taskbar), so a
            // window resting on the floor doesn't emit a phantom edge under it.
            foreach (var gr in grounds)
            {
                if (gr.Width <= 0 || gr.Height <= 0) continue;
                if (Covers(gr.Top, gr.Bottom, w.Top)) topHoles.Add((gr.Left, gr.Right));
                if (Covers(gr.Top, gr.Bottom, w.Bottom)) bottomHoles.Add((gr.Left, gr.Right));
                if (Covers(gr.Left, gr.Right, w.Left)) leftHoles.Add((gr.Top, gr.Bottom));
                if (Covers(gr.Left, gr.Right, w.Right)) rightHoles.Add((gr.Top, gr.Bottom));
            }

            // Windows in front of w (earlier in Z-order) occlude its edges.
            for (int j = 0; j < i; j++)
            {
                var o = wins[j];
                if (o.Width <= 1 || o.Height <= 1) continue;
                if (Covers(o.Top, o.Bottom, w.Top)) topHoles.Add((o.Left, o.Right));
                if (Covers(o.Top, o.Bottom, w.Bottom)) bottomHoles.Add((o.Left, o.Right));
                if (Covers(o.Left, o.Right, w.Left)) leftHoles.Add((o.Top, o.Bottom));
                if (Covers(o.Left, o.Right, w.Right)) rightHoles.Add((o.Top, o.Bottom));
            }

            foreach (var (s, e) in VisibleSegments(w.Left, w.Right, topHoles))
                platforms.Add(new Platform(w.Top, s, e));
            foreach (var (s, e) in VisibleSegments(w.Left, w.Right, bottomHoles))
                platforms.Add(new Platform(w.Bottom, s, e));
            foreach (var (s, e) in VisibleSegments(w.Top, w.Bottom, leftHoles))
                ladders.Add(new Ladder(w.Left, s, e));
            foreach (var (s, e) in VisibleSegments(w.Top, w.Bottom, rightHoles))
                ladders.Add(new Ladder(w.Right, s, e));
        }

        // Keep only the parts inside the visible screen, so the pet never targets or walks onto a
        // section of a window that is dragged partially off-screen.
        return new World(ClipPlatforms(platforms, screen), ClipLadders(ladders, screen));
    }

    /// <summary>Clamp each platform's x-span to the screen; drop edges above/below it or too short.</summary>
    private static List<Platform> ClipPlatforms(List<Platform> platforms, Rect screen)
    {
        var result = new List<Platform>(platforms.Count);
        foreach (var p in platforms)
        {
            if (p.Y < screen.Top - Eps || p.Y > screen.Bottom + Eps) continue;
            double xs = Math.Max(p.XStart, screen.Left);
            double xe = Math.Min(p.XEnd, screen.Right);
            if (xe - xs >= MinSegment) result.Add(new Platform(p.Y, xs, xe));
        }
        return result;
    }

    /// <summary>Clamp each ladder's y-span to the screen; drop edges left/right of it or too short.</summary>
    private static List<Ladder> ClipLadders(List<Ladder> ladders, Rect screen)
    {
        var result = new List<Ladder>(ladders.Count);
        foreach (var l in ladders)
        {
            if (l.X < screen.Left - Eps || l.X > screen.Right + Eps) continue;
            double yt = Math.Max(l.YTop, screen.Top);
            double yb = Math.Min(l.YBottom, screen.Bottom);
            if (yb - yt >= MinSegment) result.Add(new Ladder(l.X, yt, yb));
        }
        return result;
    }

    private static bool Covers(double lo, double hi, double v) => v >= lo - Eps && v <= hi + Eps;

    /// <summary>Subtract <paramref name="holes"/> from [start,end]; return remaining runs ≥ MinSegment.</summary>
    private static List<(double, double)> VisibleSegments(double start, double end, List<(double, double)> holes)
    {
        var result = new List<(double, double)>();
        if (end - start < MinSegment) return result;

        var clipped = new List<(double s, double e)>(holes.Count);
        foreach (var h in holes)
        {
            double hs = Math.Max(start, Math.Min(h.Item1, h.Item2));
            double he = Math.Min(end, Math.Max(h.Item1, h.Item2));
            if (he > hs) clipped.Add((hs, he));
        }
        clipped.Sort((a, b) => a.s.CompareTo(b.s));

        double cursor = start;
        foreach (var h in clipped)
        {
            if (h.s - cursor >= MinSegment) result.Add((cursor, h.s));
            if (h.e > cursor) cursor = h.e;
        }
        if (end - cursor >= MinSegment) result.Add((cursor, end));
        return result;
    }
}
