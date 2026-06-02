namespace MaplePet.Engine;

/// <summary>
/// Pure geometry/physics helpers shared by every pet state. No OS calls, no mutation.
/// </summary>
public static class Physics
{
    public const double Eps = 0.5;

    /// <summary>
    /// The platform the pet is currently resting on: one whose X-range covers
    /// <paramref name="centerX"/> and whose Y is within <paramref name="tol"/> of the feet.
    /// Returns the closest match, or null if the pet is unsupported (it should fall).
    /// </summary>
    public static Platform? FindSupport(World world, double centerX, double feetY, double tol)
    {
        Platform? best = null;
        double bestDy = double.MaxValue;
        foreach (var p in world.Platforms)
        {
            if (!p.ContainsX(centerX, Eps)) continue;
            double dy = Math.Abs(p.Y - feetY);
            if (dy <= tol && dy < bestDy)
            {
                bestDy = dy;
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// The highest platform crossed while the feet move from <paramref name="fromFeetY"/>
    /// down to <paramref name="toFeetY"/> at <paramref name="centerX"/>. Null if none.
    /// </summary>
    public static Platform? FindLanding(World world, double centerX, double fromFeetY, double toFeetY)
    {
        Platform? landing = null;
        double bestY = double.MaxValue;
        foreach (var p in world.Platforms)
        {
            if (!p.ContainsX(centerX, Eps)) continue;
            if (p.Y >= fromFeetY - 0.01 && p.Y <= toFeetY + 0.01 && p.Y < bestY)
            {
                bestY = p.Y;
                landing = p;
            }
        }
        return landing;
    }

    /// <summary>True if some platform lies strictly below the feet at this X — i.e. the edge is a
    /// cliff the pet can drop off, not solid ground.</summary>
    public static bool HasPlatformBelow(World world, double centerX, double feetY)
    {
        foreach (var p in world.Platforms)
            if (p.Y > feetY + Eps && p.ContainsX(centerX, Eps))
                return true;
        return false;
    }

    /// <summary>
    /// The live ladder segment at horizontal <paramref name="x"/> whose span contains
    /// <paramref name="feetY"/> (within <paramref name="yTol"/>). Used to re-resolve the climbed
    /// ladder every tick — if it returns null the ladder vanished and the pet must fall.
    /// </summary>
    public static Ladder? FindLadderAt(World world, double x, double feetY, double xTol, double yTol, int dir = 0)
    {
        Ladder? best = null;
        double bestScore = double.MaxValue;
        foreach (var l in world.Ladders)
        {
            double dx = Math.Abs(l.X - x);
            if (dx > xTol) continue;
            if (feetY < l.YTop - yTol || feetY > l.YBottom + yTol) continue;

            // At a shared-x junction of two abutting ladders, prefer the segment that extends in
            // the climb direction (up = above the feet, down = below) so we don't pick the wrong one.
            bool aligned =
                (dir < 0 && feetY > l.YTop + Eps) ||
                (dir > 0 && feetY < l.YBottom - Eps);
            double score = (aligned ? 0.0 : 1_000_000.0) + dx;
            if (score < bestScore)
            {
                bestScore = score;
                best = l;
            }
        }
        return best;
    }
}
