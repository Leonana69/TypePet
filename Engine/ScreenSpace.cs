namespace TypePet.Engine;

/// <summary>
/// Converts raw OS geometry (physical pixels, absolute virtual-screen coordinates) into
/// the overlay's logical coordinate space, whose origin is the overlay window's top-left.
///
/// This is the single boundary where physical->logical conversion happens
/// (see IMPLEMENTATION_PLAN.md section 6). A single uniform <see cref="Scale"/> is assumed;
/// mixed-DPI multi-monitor is a known limitation. On macOS this is a non-issue: window/display
/// geometry is reported in points with backing scale 1, so the overlay's per-display confinement
/// and floors are exact. On Windows a secondary monitor at a different DPI than the primary is
/// mapped with the primary's scale, so its clip rect / windows can be slightly mis-sized.
/// </summary>
public sealed record ScreenSpace(double OriginX, double OriginY, double Scale)
{
    public Rect ToLogical(Rect r) => new(
        (r.X - OriginX) / Scale,
        (r.Y - OriginY) / Scale,
        r.Width / Scale,
        r.Height / Scale);

    public WorldGeometry ToLogical(WorldGeometry g)
    {
        var windows = new List<Rect>(g.Windows.Count);
        foreach (var w in g.Windows) windows.Add(ToLogical(w));
        var grounds = new List<Rect>(g.Grounds.Count);
        foreach (var gr in g.Grounds) grounds.Add(ToLogical(gr));
        return new WorldGeometry(windows, ToLogical(g.Taskbar), g.TaskbarEdge) { Grounds = grounds };
    }
}
