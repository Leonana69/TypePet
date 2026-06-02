using System.Linq;

namespace MaplePet.Engine;

/// <summary>How the pet travels along a path edge.</summary>
public enum MoveKind
{
    Walk,       // horizontal, along one platform
    ClimbUp,    // grab a ladder (jumping up to its bottom if needed) and climb up
    ClimbDown,  // climb down a ladder (dropping the last gap if the platform is below its bottom)
    DropDown,   // walk off a platform edge and fall to a lower platform
}

/// <summary>A waypoint on a platform (a specific x on a specific platform).</summary>
public readonly record struct NavNode(double X, double Y, int PlatformIndex);

/// <summary>One move to reach a node at (X,Y); <see cref="LadderIndex"/> is set for climbs.</summary>
public readonly record struct PathStep(MoveKind Kind, double X, double Y, int LadderIndex);

/// <summary>
/// A navigation graph derived from the visible <see cref="World"/> (platforms + ladders), used to
/// plan paths for roaming. Nodes are points on platforms; edges are walk / climb / drop moves.
/// Rebuilt whenever the world changes. Pure and platform-agnostic.
///
/// The move model mirrors MapleSimulator's map_def.rs (walk / jump-to-rope / climb / down-jump),
/// minus teleport. UP is only via ladders; a ladder is mountable from a platform whose top is
/// within <c>jumpHeight</c> of the ladder's bottom.
/// </summary>
public sealed class MapGraph
{
    private const double Eps = 0.5;
    private const double YTol = 8.0;     // platform/feet vertical tolerance
    private const double XMerge = 3.0;   // merge attach points closer than this
    private const double RopeBias = 2.0; // slight cost penalty so walking is preferred over climbing

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

        // Attach x-coordinates collected per platform (endpoints, ladder lines, drop landings).
        var attach = new List<List<double>>(pc);
        for (int i = 0; i < pc; i++)
            attach.Add(new List<double> { platforms[i].XStart, platforms[i].XEnd });

        // Ladder stops: platforms the ladder reaches (within its span) or can be mounted from
        // (a platform up to jumpHeight below the ladder bottom).
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

        // Down-jump landings: from each platform endpoint, the highest platform below at that x.
        var drops = new List<(int from, double x, int to)>();
        for (int pi = 0; pi < pc; pi++)
        {
            var p = platforms[pi];
            foreach (double ex in new[] { p.XStart, p.XEnd })
            {
                int below = HighestBelow(platforms, ex, p.Y);
                if (below >= 0) { drops.Add((pi, ex, below)); attach[below].Add(ex); }
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

        // Climb edges between consecutive stops along each ladder.
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
                g.AddEdge(upId, loId, MoveKind.ClimbDown, cost, li);
            }
        }

        // Walk-across edges (seam x's were registered into attach[] above, so these nodes are
        // already part of each platform's walk chain).
        foreach (var (i, j, x) in across)
        {
            g.AddEdge(NodeFor(i, x), NodeFor(j, x), MoveKind.Walk, 1, -1);
            g.AddEdge(NodeFor(j, x), NodeFor(i, x), MoveKind.Walk, 1, -1);
        }

        // Down-jump edges.
        foreach (var (from, x, to) in drops)
        {
            int fromId = NodeFor(from, x);
            int toId = NodeFor(to, x);
            double cost = System.Math.Abs(platforms[to].Y - platforms[from].Y) + 4;
            g.AddEdge(fromId, toId, MoveKind.DropDown, cost, -1);
        }

        return g;
    }

    private static bool XRangesTouch(Platform a, Platform b)
    {
        double lo = System.Math.Max(a.XStart, b.XStart);
        double hi = System.Math.Min(a.XEnd, b.XEnd);
        return hi >= lo - 2.0; // overlap or touch within 2px
    }

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

    /// <summary>Node ids reachable up to <paramref name="roamingHeightPct"/> above the ground.</summary>
    public List<int> EligibleTargets(double roamingHeightPct)
    {
        double maxHeight = GroundY * (roamingHeightPct / 100.0);
        var result = new List<int>();
        for (int i = 0; i < _nodes.Count; i++)
            if (GroundY - _nodes[i].Y <= maxHeight + Eps)
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
