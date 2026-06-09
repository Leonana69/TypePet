namespace TypePet.Engine;

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

    /// <summary>A solved ballistic leap: an upward launch speed, a (signed) horizontal speed, and
    /// the time of flight until the feet reach the target. <c>Vel = (Vx, -LaunchVy)</c>.</summary>
    public readonly record struct GapJumpArc(double LaunchVy, double Vx, double Time);

    /// <summary>How far below the source lip a level/downward gap-jump sinks its feet before launching,
    /// so the platform it just left isn't immediately re-detected as the landing (mirrors the
    /// down-jump nudge). Shared by the planner's reachability sim and the live launch so the two
    /// agree.</summary>
    public const double GapLaunchSink = 2.0;

    /// <summary>How far PAST the lip an edge-drop launches (just beyond <see cref="Eps"/>), so the
    /// platform it steps off is outside <see cref="Platform.ContainsX"/>'s tolerance from the very
    /// first frame — clearing the lip independently of frame rate / walk speed. Shared by the
    /// planner's reachability sim and the live launch so the two agree. A same-height neighbour that
    /// starts at the lip still catches the step (it's wider than this), so walk-across is preserved.</summary>
    public const double EdgeLipClear = 1.0;

    /// <summary>The feet-Y a gap-jump actually launches from: nudged just under the source lip for a
    /// same-height-or-lower leap (which descends back toward the launch level), unchanged for an
    /// upward leap (which rises clear of the lip on its own).</summary>
    public static double GapJumpLaunchFeet(double launchY, double landY)
        => landY >= launchY - Eps ? launchY + GapLaunchSink : launchY;

    /// <summary>
    /// Solve the lateral leap that launches from (<paramref name="x0"/>,<paramref name="y0"/>) and
    /// passes exactly through (<paramref name="x1"/>,<paramref name="y1"/>) — used to cross a
    /// horizontal GAP onto a detached platform beside / below / above the current one. The pet leaps
    /// at its run speed (<paramref name="walkSpeed"/>) so the flight time follows from the gap width;
    /// the upward launch speed is then whatever lands it on the target. Returns null when the leap
    /// would have to rise more than <paramref name="maxRise"/> (the gap is too big to clear) — this
    /// is the "if the gap isn't too big" gate. Pure physics; no world/collision check here.
    /// </summary>
    public static GapJumpArc? SolveGapJump(double x0, double y0, double x1, double y1,
        double gravity, double walkSpeed, double maxRise)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;             // > 0 when the target is lower (a drop)
        double adx = Math.Abs(dx);
        if (adx < Eps) return null;      // not a horizontal gap

        double g = Math.Max(1.0, gravity);
        double w = Math.Max(1.0, walkSpeed);
        double t = adx / w;              // fly the gap at run speed
        // Upward launch speed that puts the feet on (x1,y1) at time t (screen y grows downward):
        //   dy = -v0*t + ½ g t²  ->  v0 = ½ g t - dy/t.
        double v0 = 0.5 * g * t - dy / t;

        if (v0 <= 0.0)
        {
            // The target is low/near enough that no hop is needed — run off the lip and fall. Falling
            // straight off takes sqrt(2·dy/g); the horizontal speed to span the gap in that time is
            // necessarily ≤ run speed, so the pet drifts off rather than dashing.
            if (dy <= Eps) return null;  // a same/higher target can't be reached without a hop
            t = Math.Sqrt(2.0 * dy / g);
            return new GapJumpArc(0.0, dx / t, t);
        }

        if (v0 * v0 > 2.0 * g * maxRise) return null; // apex would exceed the pet's max jump height
        return new GapJumpArc(v0, dx / t, t);
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
