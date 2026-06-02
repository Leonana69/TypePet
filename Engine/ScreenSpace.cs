namespace MaplePet.Engine;

/// <summary>
/// Converts raw OS geometry (physical pixels, absolute virtual-screen coordinates) into
/// the overlay's logical coordinate space, whose origin is the overlay window's top-left.
///
/// This is the single boundary where physical->logical conversion happens
/// (see IMPLEMENTATION_PLAN.md section 6). For v1 a single uniform <see cref="Scale"/>
/// is assumed; mixed-DPI multi-monitor is a known limitation.
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
        return new WorldGeometry(windows, ToLogical(g.Taskbar), g.TaskbarEdge);
    }
}
