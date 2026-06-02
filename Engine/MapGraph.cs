using System.Linq;

namespace MaplePet.Engine;

/// <summary>How the pet travels along a path edge.</summary>
public enum MoveKind
{
    Walk,     // horizontal, along one platform
    ClimbUp,  // grab a ladder (jumping up to its bottom if needed) and climb up
    JumpUp,   // hop straight up onto a higher platform within jump height (a parabola)
    DropDown, // down-jump: drop through the current platform onto the one below (a parabola)
}

/// <summary>A waypoint on a platform (a specific x on a specific platform).</summary>
public readonly record struct NavNode(double X, double Y, int PlatformIndex);

/// <summary>One move to reach a node at (X,Y); <see cref="LadderIndex"/> is set for climbs.</summary>
public readonly record struct PathStep(MoveKind Kind, double X, double Y, int LadderIndex);

/// <summary>
/// A navigation graph derived from the visible <see cref="World"/> (platforms + ladders), used to
/// plan paths for roaming. Nodes are points on platforms; edges are walk / climb-up / jump-up /
/// down-jump moves. Rebuilt whenever the world changes. Pure and platform-agnostic.
///
/// The move model mirrors MapleSimulator's map_def.rs minus teleport, with two intentional rules
/// from the latest plan:
///   * UP within <c>jumpHeight</c> is a direct <see cref="MoveKind.JumpUp"/> between overlapping
///     platforms (no ladder needed); only gaps taller than that require a ladder (ClimbUp).
///   * DOWN is always a <see cref="MoveKind.DropDown"/> through the current platform onto the one
///     directly below — ladders are never climbed down.
/// </summary>
public sealed class MapGraph
{
    private const double Eps = 0.5;
    private const double YTol = 8.0;       // platform/feet vertical tolerance
    private const double XMerge = 3.0;     // merge attach points closer than this
    private const double RopeBias = 2.0;   // cost penalty so walking is preferred over climbing
    private const double JumpBias = 0.5;   // < RopeBias: a within-reach jump always beats a ladder
    private const double DropBias = 1.0;   // cost penalty for a down-jump
    private const double MinOverlap = 12.0; // need at least this much shared x to jump/drop between
    private const double HopDist = 28.0;    // horizontal span of a jump/drop arc (bounded for sane vx)

    private readonly World _world;
    private readonly List<NavNode> _nodes = new();
    private readonly List<List<(int to, MoveKind kind, double cost, int ladder)>> _adj = new();

    public IReadOnlyList<NavNode> Nodes => _nodes;
    public World World => _world;

    /// <summary>The ground level (taskbar top): the largest platform Y. Height % is measured from here.</summary>
    public double GroundY { get; }

    private MapGraph(World world)
    {
        _world = world;
        double ground = 0;
        foreach (var p in world.Platforms) ground = System.Math.Max(ground, p.Y);
        GroundY = ground;
    }

    private int AddNode(NavNode n) { _nodes.Add(n); _adj.Add(new()); return _nodes.Count - 1; }
    private void AddEdge(int from, int to, MoveKind kind, double cost, int ladder)
        => _adj[from].Add((to, kind, cost, ladder));

    public static MapGraph Build(World world, double jumpHeight)
    {
        var g = new MapGraph(world);
        var platforms = world.Platforms;
        var ladders = world.Ladders;
        int pc = platforms.Count;

        // Attach x-coordinates collected per platform (endpoints, ladder lines, jump/drop points).
        var attach = new List<List<double>>(pc);
        for (int i = 0; i < pc; i++)
            attach.Add(new List<double> { platforms[i].XStart, platforms[i].XEnd });

        // Ladder stops: platforms the ladder reaches (within its span) or can be mounted from
        // (a platform up to jumpHeight below the ladder bottom). Used for UP climbs only.
        var ladderStops = new List<List<int>>(ladders.Count);
        for (int li = 0; li < ladders.Count; li++)
        {
            var l = ladders[li];
            var stops = new List<int>();
            for (int pi = 0; pi < pc; pi++)
            {
                var p = platforms[pi];
                if (!p.ContainsX(l.X, Eps)) continue;
                if (p.Y >= l.YTop - YTol && p.Y <= l.YBottom + jumpHeight)
                {
                    stops.Add(pi);
                    attach[pi].Add(l.X);
                }
            }
            stops.Sort((a, b) => platforms[a].Y.CompareTo(platforms[b].Y)); // top (small Y) first
            ladderStops.Add(stops);
        }

        // Jump-up edges: A -> B where B is the lowest platform above A, within jumpHeight, over a
        // horizontal overlap. Down-jump edges: A -> B where B is the highest platform below A
        // (drop straight through A). Both register two orientations so the arc can lean left or
        // right; each orientation is kept only if nothing else sits between A and B at those x's.
        var jumps = new List<(int from, double fromX, int to, double toX)>();
        var drops = new List<(int from, double fromX, int to, double toX)>();
        for (int a = 0; a < pc; a++)
            for (int b = 0; b < pc; b++)
            {
                if (a == b) continue;
                var pa = platforms[a];
                var pb = platforms[b];
                double lo = System.Math.Max(pa.XStart, pb.XStart);
                double hi = System.Math.Min(pa.XEnd, pb.XEnd);
                if (hi - lo <= Eps) continue; // need at least a shared column

                double up = pa.Y - pb.Y;   // > 0 when B is above A
                double down = pb.Y - pa.Y; // > 0 when B is below A

                if (up > YTol && up <= jumpHeight)
                {
                    // A jump needs a real footprint to land on; a thin sliver isn't enough.
                    if (hi - lo < MinOverlap) continue;
                    foreach (var (lx, tx) in ValidHops(lo, hi, x => LowestAbove(platforms, x, pa.Y) == b))
                    {
                        attach[a].Add(lx); attach[b].Add(tx);
                        jumps.Add((a, lx, b, tx));
                    }
                }
                else if (down > YTol)
                {
                    // A down-jump only needs a shared column to fall through — no MinOverlap gate,
                    // otherwise a platform a ladder can climb ONTO (ladders need no overlap) but that
                    // overlaps the floor below by < MinOverlap would have no way back down (soft-lock).
                    foreach (var (lx, tx) in ValidHops(lo, hi, x => HighestBelow(platforms, x, pa.Y) == b))
                    {
                        attach[a].Add(lx); attach[b].Add(tx);
                        drops.Add((a, lx, b, tx));
                    }
                }
            }

        // Walk-across seams between abutting/overlapping same-height platforms. Register the seam x
        // into BOTH platforms' attach lists now, so each seam node is spliced into that platform's
        // walk chain below (otherwise an interior seam becomes an orphan with no walk edges).
        var across = new List<(int i, int j, double x)>();
        for (int i = 0; i < pc; i++)
            for (int j = i + 1; j < pc; j++)
                if (System.Math.Abs(platforms[i].Y - platforms[j].Y) <= YTol && XRangesTouch(platforms[i], platforms[j]))
                {
                    double x = System.Math.Max(platforms[i].XStart, platforms[j].XStart);
                    attach[i].Add(x);
                    attach[j].Add(x);
                    across.Add((i, j, x));
                }

        // Create platform-point nodes (deduped) and walk edges between consecutive ones.
        var nodeOf = new Dictionary<(int plat, long key), int>();
        int NodeFor(int pi, double x)
        {
            long key = (long)System.Math.Round(x / XMerge);
            if (nodeOf.TryGetValue((pi, key), out int id)) return id;
            id = g.AddNode(new NavNode(x, platforms[pi].Y, pi));
            nodeOf[(pi, key)] = id;
            return id;
        }

        for (int pi = 0; pi < pc; pi++)
        {
            var ordered = attach[pi]
                .GroupBy(x => (long)System.Math.Round(x / XMerge))
                .Select(grp => grp.First())
                .OrderBy(x => x)
                .ToList();
            var ids = ordered.Select(x => NodeFor(pi, x)).ToList();
            for (int k = 0; k + 1 < ids.Count; k++)
            {
                double dx = System.Math.Abs(g._nodes[ids[k + 1]].X - g._nodes[ids[k]].X);
                g.AddEdge(ids[k], ids[k + 1], MoveKind.Walk, dx, -1);
                g.AddEdge(ids[k + 1], ids[k], MoveKind.Walk, dx, -1);
            }
        }

        // Climb-up edges between consecutive stops along each ladder (UP only — never climb down).
        for (int li = 0; li < ladders.Count; li++)
        {
            var l = ladders[li];
            var stops = ladderStops[li];
            for (int k = 0; k + 1 < stops.Count; k++)
            {
                int upper = stops[k];     // higher (smaller Y)
                int lower = stops[k + 1]; // lower (larger Y)
                int upId = NodeFor(upper, l.X);
                int loId = NodeFor(lower, l.X);
                double cost = System.Math.Abs(platforms[lower].Y - platforms[upper].Y) + RopeBias;
                g.AddEdge(loId, upId, MoveKind.ClimbUp, cost, li);
            }
        }

        // Walk-across edges (seam x's were registered into attach[] above).
        foreach (var (i, j, x) in across)
        {
            g.AddEdge(NodeFor(i, x), NodeFor(j, x), MoveKind.Walk, 1, -1);
            g.AddEdge(NodeFor(j, x), NodeFor(i, x), MoveKind.Walk, 1, -1);
        }

        // Jump-up edges. Cost = vertical gap + a small bias (no horizontal term), kept strictly
        // below the same-gap ladder cost so a within-reach platform is always jumped to, never
        // climbed (plan item 3).
        foreach (var (from, fx, to, tx) in jumps)
        {
            double cost = (platforms[from].Y - platforms[to].Y) + JumpBias;
            g.AddEdge(NodeFor(from, fx), NodeFor(to, tx), MoveKind.JumpUp, cost, -1);
        }

        // Down-jump edges.
        foreach (var (from, fx, to, tx) in drops)
        {
            double cost = (platforms[to].Y - platforms[from].Y) + DropBias;
            g.AddEdge(NodeFor(from, fx), NodeFor(to, tx), MoveKind.DropDown, cost, -1);
        }

        return g;
    }

    /// <summary>
    /// The valid launch/landing x-pairs for a jump/drop across overlap [lo,hi]. The hop is a fixed
    /// modest width centered in the overlap (lean right and lean left), so a small vertical gap
    /// can't produce an absurd horizontal velocity on a wide platform. A pair is kept only if the
    /// destination is clear (nothing between) at BOTH its x's per <paramref name="clear"/>.
    /// Preference order: a centered leaning arc, else a straight vertical hop at the center, else —
    /// if partial occlusion hid the center — a scan across the overlap for ANY clear column. So as
    /// long as a clear column exists anywhere in the overlap, the edge is emitted.
    /// </summary>
    private static IEnumerable<(double launchX, double landX)> ValidHops(double lo, double hi, System.Func<double, bool> clear)
    {
        double center = (lo + hi) / 2.0;
        double half = System.Math.Min(HopDist / 2.0, (hi - lo) / 2.0 - 0.5);

        // Preferred: a centered leaning arc with both ends clear.
        if (half >= 1.0 && clear(center - half) && clear(center + half))
        {
            yield return (center - half, center + half);
            yield return (center + half, center - half);
            yield break;
        }
        // Next: a straight vertical hop at the center.
        if (clear(center)) { yield return (center, center); yield break; }

        // Fallback: the center is occluded — scan for an off-center clear column.
        double step = System.Math.Max(2.0, (hi - lo) / 32.0);
        if (half >= 1.0)
            for (double x = lo + 0.5; x <= hi - 0.5; x += step)
                if (x - half >= lo && x + half <= hi && clear(x - half) && clear(x + half))
                {
                    yield return (x - half, x + half);
                    yield return (x + half, x - half);
                    yield break;
                }
        for (double x = lo + 0.5; x <= hi - 0.5; x += step)
            if (clear(x)) { yield return (x, x); yield break; }
    }

    private static bool XRangesTouch(Platform a, Platform b)
    {
        double lo = System.Math.Max(a.XStart, b.XStart);
        double hi = System.Math.Min(a.XEnd, b.XEnd);
        return hi >= lo - 2.0; // overlap or touch within 2px
    }

    /// <summary>The platform directly below (x,y): the one containing x with the smallest Y &gt; y.</summary>
    private static int HighestBelow(IReadOnlyList<Platform> platforms, double x, double y)
    {
        int best = -1; double bestY = double.MaxValue;
        for (int i = 0; i < platforms.Count; i++)
        {
            var p = platforms[i];
            if (!p.ContainsX(x, Eps)) continue;
            if (p.Y > y + YTol && p.Y < bestY) { bestY = p.Y; best = i; }
        }
        return best;
    }

    /// <summary>The platform directly above (x,y): the one containing x with the largest Y &lt; y.</summary>
    private static int LowestAbove(IReadOnlyList<Platform> platforms, double x, double y)
    {
        int best = -1; double bestY = double.MinValue;
        for (int i = 0; i < platforms.Count; i++)
        {
            var p = platforms[i];
            if (!p.ContainsX(x, Eps)) continue;
            if (p.Y < y - YTol && p.Y > bestY) { bestY = p.Y; best = i; }
        }
        return best;
    }

    /// <summary>The platform index the pet stands on at (x, feetY), or -1.</summary>
    public int PlatformAt(double x, double feetY)
    {
        int best = -1; double bestDy = YTol;
        for (int i = 0; i < _world.Platforms.Count; i++)
        {
            var p = _world.Platforms[i];
            if (!p.ContainsX(x, Eps)) continue;
            double dy = System.Math.Abs(p.Y - feetY);
            if (dy <= bestDy) { bestDy = dy; best = i; }
        }
        return best;
    }

    /// <summary>The node nearest to a position (used to resolve a target into a graph node).</summary>
    public int NearestNode(double x, double y)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < _nodes.Count; i++)
        {
            double dx = _nodes[i].X - x, dy = _nodes[i].Y - y;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>The node on platform <paramref name="pi"/> closest in x, or -1 if it has none.</summary>
    public int NearestNodeOnPlatform(int pi, double x)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].PlatformIndex != pi) continue;
            double d = System.Math.Abs(_nodes[i].X - x);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Platform indices whose top edge is no higher than <paramref name="roamingHeightPct"/>.</summary>
    public List<int> EligiblePlatforms(double roamingHeightPct)
    {
        double maxHeight = GroundY * (roamingHeightPct / 100.0);
        var result = new List<int>();
        for (int i = 0; i < _world.Platforms.Count; i++)
            if (GroundY - _world.Platforms[i].Y <= maxHeight + Eps)
                result.Add(i);
        return result;
    }

    /// <summary>Dijkstra from the pet's position to <paramref name="goal"/>; null if unreachable.</summary>
    public List<PathStep>? FindPath(double startX, double startFeetY, int goal)
    {
        int n = _nodes.Count;
        if (goal < 0 || goal >= n) return null;
        int startPlat = PlatformAt(startX, startFeetY);
        if (startPlat < 0) return null;

        var dist = new double[n];
        var predNode = new int[n];
        var predKind = new MoveKind[n];
        var predLadder = new int[n];
        for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; predNode[i] = -2; }

        var pq = new PriorityQueue<int, double>();
        for (int i = 0; i < n; i++)
            if (_nodes[i].PlatformIndex == startPlat)
            {
                double d = System.Math.Abs(_nodes[i].X - startX);
                dist[i] = d; predNode[i] = -1; predKind[i] = MoveKind.Walk; predLadder[i] = -1;
                pq.Enqueue(i, d);
            }

        while (pq.TryDequeue(out int u, out double du))
        {
            if (du > dist[u]) continue;
            if (u == goal) break;
            foreach (var (to, kind, cost, ladder) in _adj[u])
            {
                double nd = du + cost;
                if (nd < dist[to])
                {
                    dist[to] = nd; predNode[to] = u; predKind[to] = kind; predLadder[to] = ladder;
                    pq.Enqueue(to, nd);
                }
            }
        }

        if (double.IsInfinity(dist[goal])) return null;

        var steps = new List<PathStep>();
        for (int cur = goal; cur != -1; cur = predNode[cur])
        {
            if (cur == -2) return null; // corrupt chain (shouldn't happen)
            var node = _nodes[cur];
            steps.Add(new PathStep(predKind[cur], node.X, node.Y, predLadder[cur]));
        }
        steps.Reverse();
        return steps;
    }
}
