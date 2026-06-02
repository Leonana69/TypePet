using System;

namespace MaplePet.Engine;

public enum PetState
{
    Stand, // idle, standing still on a platform
    Walk,  // walking left/right on a platform
    Rope,  // on a ladder (a window's side edge), climbing up or down
    Jump,  // airborne: jumping, falling, or being dragged
}

/// <summary>
/// The pet's brain + physics. It treats the visible platforms/ladders as a navigation map
/// (<see cref="MapGraph"/>): it picks a random reachable target (no higher than
/// <c>RoamingHeight</c>% of the screen), plans a path of walk / climb / drop moves, and follows
/// it, idling (STAND) between trips. The world is re-resolved every tick — if a window moves or
/// closes, the graph is rebuilt and the route is replanned; if the surface it stands on or the
/// ladder it climbs disappears, it falls and replans on landing.
///
/// Position is the pet's top-left corner, in logical pixels.
/// </summary>
public sealed class PetController
{
    private readonly Settings _cfg;
    private readonly Random _rng;

    private const double SupportTol = 8.0;  // feet-to-platform snap tolerance
    private const double LadderXTol = 5.0;  // how near the ladder line counts as "on" it
    private const double ArriveTol = 1.5;   // horizontal "reached the waypoint" tolerance
    private const double IdleMinSeconds = 0.8;
    private const double IdleMaxSeconds = 2.4;
    private const int TargetTries = 16;        // attempts to find a reachable random target
    private const double TargetEdgeMargin = 18;// keep random targets off the very corners
    private const double JumpClearance = 12;   // apex height above the platform we jump up onto
    private const double DropClearance = 2;    // sink below the source platform before a down-jump

    public Vec2 Pos;          // top-left, logical px
    public Vec2 Size;         // width/height, logical px
    public Vec2 Vel;          // px/second
    public PetState State { get; private set; } = PetState.Jump;
    public int Facing { get; private set; } = 1; // +1 = right, -1 = left
    public bool IsDragging { get; private set; }

    // Navigation
    private MapGraph? _graph;
    private World? _graphWorld;
    private List<PathStep>? _path;
    private int _step;
    private Vec2 _targetPos;
    private bool _hasTarget;
    private bool _pendingReplan;

    private bool _spawned;
    private double _idleTimer;
    private bool _standAfterLanding; // after a drag-release, stand where it lands

    // Rope execution
    private double _ropeX;
    private double _ropeTargetY;
    private int _ropeDir; // -1 up, +1 down

    public double FeetY => Pos.Y + Size.Y;
    public double CenterX => Pos.X + Size.X / 2;

    public IReadOnlyList<PathStep>? Path => _path;
    public Vec2 TargetPos => _targetPos;
    public bool HasTarget => _hasTarget;

    public PetController(Settings cfg, Vec2 size, int? seed = null)
    {
        _cfg = cfg;
        Size = size;
        _rng = seed is int s ? new Random(s) : new Random();
    }

    public void Update(World world, double dt)
    {
        if (world.Platforms.Count == 0) return;
        if (IsDragging) return; // position is driven by the cursor while held

        EnsureSpawn(world);

        // Rebuild the nav graph whenever the world changes (windows moved/opened/closed).
        if (!ReferenceEquals(world, _graphWorld))
        {
            _graph = MapGraph.Build(world, _cfg.JumpHeight);
            _graphWorld = world;
            _pendingReplan = true;
        }

        switch (State)
        {
            case PetState.Stand: UpdateStand(world, dt); break;
            case PetState.Walk: UpdateWalk(world, dt); break;
            case PetState.Rope: UpdateRope(world, dt); break;
            case PetState.Jump: UpdateJump(world, dt); break;
        }
    }

    private void EnsureSpawn(World world)
    {
        if (_spawned) return;
        Platform ground = world.Platforms[0];
        foreach (var p in world.Platforms)
            if (p.Y > ground.Y) ground = p;

        Pos = new Vec2(ground.CenterX - Size.X / 2, ground.Y - Size.Y);
        Vel = default;
        Facing = 1;
        _spawned = true;
        EnterStand(0.3);
    }

    // ---------------------------------------------------------------- Drag
    public void BeginDrag()
    {
        IsDragging = true;
        State = PetState.Jump;
        Vel = default;
    }

    public void DragTo(Vec2 center)
    {
        if (IsDragging)
            Pos = new Vec2(center.X - Size.X / 2, center.Y - Size.Y / 2);
    }

    public void EndDrag()
    {
        IsDragging = false;
        State = PetState.Jump;
        Vel = default;
        _standAfterLanding = true;
        _path = null;
        _hasTarget = false;
    }

    // ---------------------------------------------------------------- Stand (idle)
    private void EnterStand(double seconds = -1)
    {
        State = PetState.Stand;
        Vel = default;
        _path = null;
        _hasTarget = false;
        _idleTimer = seconds >= 0 ? seconds : IdleMinSeconds + _rng.NextDouble() * (IdleMaxSeconds - IdleMinSeconds);
    }

    private void UpdateStand(World world, double dt)
    {
        var support = Physics.FindSupport(world, CenterX, FeetY, SupportTol);
        if (support is null) { BeginFall(); return; }
        Pos = new Vec2(Pos.X, support.Value.Y - Size.Y);

        _idleTimer -= dt;
        if (_idleTimer <= 0)
        {
            if (PickTarget(world)) State = PetState.Walk;
            else _idleTimer = 0.5; // nothing reachable yet; try again shortly
        }
    }

    // ---------------------------------------------------------------- Target selection
    private bool PickTarget(World world)
    {
        if (_graph is null) return false;
        var plats = _graph.EligiblePlatforms(_cfg.RoamingHeight);
        if (plats.Count == 0) return false;

        for (int attempt = 0; attempt < TargetTries; attempt++)
        {
            var p = world.Platforms[plats[_rng.Next(plats.Count)]];
            // A random point INSIDE the top edge, not a corner.
            double margin = Math.Min(TargetEdgeMargin, p.Width / 3.0);
            double inner = p.Width - 2 * margin;
            double tx = inner > 1 ? p.XStart + margin + _rng.NextDouble() * inner : p.CenterX;

            // Skip targets we're basically already at. Bound the skip radius below half the interior
            // band, otherwise on a narrow platform it would reject the whole band and the pet (when
            // this is its only roamable platform) would idle forever.
            double skipR = Math.Min(24.0, Math.Max(ArriveTol, inner / 2.0 - ArriveTol));
            if (Math.Abs(tx - CenterX) < skipR && Math.Abs(p.Y - FeetY) < SupportTol) continue;

            var target = new Vec2(tx, p.Y);
            var path = PlanTo(target);
            if (path is { Count: > 0 })
            {
                _path = path;
                _step = 0;
                _targetPos = target;
                _hasTarget = true;
                _pendingReplan = false;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Plan a route from the pet's current position to <paramref name="target"/>, a point that may
    /// be in the interior of its platform. We path to the nearest graph node on the target platform,
    /// then append a final walk to the exact target x so the pet stops at the chosen spot.
    /// </summary>
    private List<PathStep>? PlanTo(Vec2 target)
    {
        if (_graph is null) return null;
        int pi = _graph.PlatformAt(target.X, target.Y);
        int goal = pi >= 0 ? _graph.NearestNodeOnPlatform(pi, target.X)
                           : _graph.NearestNode(target.X, target.Y);
        if (goal < 0) return null;

        var path = _graph.FindPath(CenterX, FeetY, goal);
        if (path is null) return null;

        if (path.Count == 0 || Math.Abs(path[^1].X - target.X) > ArriveTol)
            path.Add(new PathStep(MoveKind.Walk, target.X, target.Y, -1));
        return path;
    }

    private void RecomputePath(World world)
    {
        _pendingReplan = false;
        if (!_hasTarget || _graph is null) { _path = null; return; }
        _path = PlanTo(_targetPos);
        _step = 0;
        if (_path is null) _hasTarget = false; // target no longer reachable; idle/pick anew
    }

    private void OnPathDone() => EnterStand();

    private void Advance(World world)
    {
        _step++;
        if (_path is null || _step >= _path.Count) OnPathDone();
    }

    // ---------------------------------------------------------------- Walk
    private void UpdateWalk(World world, double dt)
    {
        if (_pendingReplan) RecomputePath(world);
        if (_path is null || _step >= _path.Count) { OnPathDone(); return; }

        var step = _path[_step];
        switch (step.Kind)
        {
            case MoveKind.Walk:
                if (WalkToward(world, dt, step.X)) Advance(world);
                break;
            case MoveKind.ClimbUp:
                if (WalkToward(world, dt, step.X)) StartRope(world, step);
                break;
            case MoveKind.JumpUp:
                // The previous step walked us to the launch x; arc up onto (step.X, step.Y).
                LaunchArc(step.X, step.Y);
                break;
            case MoveKind.DropDown:
                // Down-jump: drop through this platform and arc onto (step.X, step.Y) below.
                BeginDrop(step.X, step.Y);
                break;
        }
    }

    /// <summary>Walk toward <paramref name="targetX"/> on the current platform. True once reached.</summary>
    private bool WalkToward(World world, double dt, double targetX)
    {
        var support = Physics.FindSupport(world, CenterX, FeetY, SupportTol);
        if (support is null) { BeginFall(); return false; } // lost the ground
        Pos = new Vec2(Pos.X, support.Value.Y - Size.Y);

        double delta = targetX - CenterX;
        if (Math.Abs(delta) <= ArriveTol) return true;

        int dir = delta > 0 ? 1 : -1;
        Facing = dir;
        double advance = _cfg.WalkSpeed * dt;
        double nextCenter = CenterX + dir * advance;
        if ((dir > 0 && nextCenter >= targetX) || (dir < 0 && nextCenter <= targetX))
        {
            Pos = new Vec2(targetX - Size.X / 2, Pos.Y);
            return true;
        }
        Pos = new Vec2(Pos.X + dir * advance, Pos.Y);
        return false;
    }

    // ---------------------------------------------------------------- Rope
    private void StartRope(World world, PathStep step)
    {
        if (step.LadderIndex < 0 || step.LadderIndex >= world.Ladders.Count) { _pendingReplan = true; return; }
        var l = world.Ladders[step.LadderIndex];
        _ropeX = l.X;
        _ropeTargetY = step.Y;
        _ropeDir = step.Y < FeetY ? -1 : 1;

        double grab = Clamp(FeetY, l.YTop, l.YBottom); // jump up to the bottom if we're below it
        Pos = new Vec2(l.X - Size.X / 2, grab - Size.Y);
        Vel = default;
        State = PetState.Rope;
    }

    private void UpdateRope(World world, double dt)
    {
        var seg = Physics.FindLadderAt(world, _ropeX, FeetY, LadderXTol, SupportTol, _ropeDir);
        if (seg is null) { BeginFall(); return; } // ladder vanished
        var l = seg.Value;
        _ropeX = l.X;

        double feet = FeetY + _ropeDir * _cfg.ClimbSpeed * dt;

        if (_ropeDir < 0) // climbing up
        {
            double top = Math.Max(_ropeTargetY, l.YTop);
            if (feet <= top) { SnapRope(top); FinishRopeOnto(world, top); return; }
        }
        else // climbing down
        {
            if (_ropeTargetY <= l.YBottom + 0.5)
            {
                if (feet >= _ropeTargetY) { SnapRope(_ropeTargetY); FinishRopeOnto(world, _ropeTargetY); return; }
            }
            else if (feet >= l.YBottom) // target platform is below the ladder bottom -> drop the gap
            {
                SnapRope(l.YBottom);
                BeginFall();
                return;
            }
        }

        SnapRope(feet);
    }

    private void SnapRope(double feet) => Pos = new Vec2(_ropeX - Size.X / 2, feet - Size.Y);

    private void FinishRopeOnto(World world, double feetY)
    {
        var support = Physics.FindSupport(world, CenterX, feetY, SupportTol);
        if (support is null) { BeginFall(); return; }
        Pos = new Vec2(Pos.X, support.Value.Y - Size.Y);
        Vel = default;
        State = PetState.Walk;
        Advance(world);
    }

    // ---------------------------------------------------------------- Jump / fall
    private void BeginFall(double vx = 0)
    {
        Vel = new Vec2(vx, 0);
        State = PetState.Jump;
    }

    /// <summary>
    /// Launch a ballistic jump UP from the current feet position, arcing onto (<paramref name="landX"/>,
    /// <paramref name="landY"/>) where landY is above. The apex clears the target by
    /// <see cref="JumpClearance"/>, so the pet rises past the platform and settles down onto it —
    /// landing is detected by the normal descent test in <see cref="UpdateJump"/>.
    /// </summary>
    private void LaunchArc(double landX, double landY)
    {
        double x0 = CenterX, y0 = FeetY;
        double g = Math.Max(1.0, _cfg.Gravity);
        double apexY = Math.Min(y0, landY) - JumpClearance; // highest point (smallest Y)
        double rise = Math.Max(1.0, y0 - apexY);            // launch -> apex
        double v0 = Math.Sqrt(2 * g * rise);                // upward launch speed
        double disc = Math.Max(0.0, v0 * v0 - 2 * g * (y0 - landY));
        double t = (v0 + Math.Sqrt(disc)) / g;              // time to descend onto landY
        double vx = t > 1e-3 ? (landX - x0) / t : 0;
        Vel = new Vec2(vx, -v0);
        State = PetState.Jump;
    }

    /// <summary>
    /// Down-jump: drop through the current platform and arc onto (<paramref name="landX"/>,
    /// <paramref name="landY"/>) below. We first sink the feet just under the source surface so
    /// <see cref="Physics.FindLanding"/> won't immediately re-detect it, then fall (no upward pop)
    /// with the horizontal velocity needed to reach landX — a downward parabola.
    /// </summary>
    private void BeginDrop(double landX, double landY)
    {
        Pos = new Vec2(Pos.X, FeetY + DropClearance - Size.Y); // sink slightly below the source
        double g = Math.Max(1.0, _cfg.Gravity);
        double fall = Math.Max(1.0, landY - FeetY);
        double t = Math.Sqrt(2 * fall / g);                    // free-fall time (vy0 = 0)
        double vx = t > 1e-3 ? (landX - CenterX) / t : 0;
        Vel = new Vec2(vx, 0);
        State = PetState.Jump;
    }

    private void UpdateJump(World world, double dt)
    {
        double vy = Vel.Y + _cfg.Gravity * dt;
        double vx = Vel.X;
        double fromFeet = FeetY;
        double newX = Pos.X + vx * dt;
        double newFeet = fromFeet + vy * dt;
        double newCenter = newX + Size.X / 2;

        var landing = Physics.FindLanding(world, newCenter, fromFeet, newFeet);
        if (landing is not null)
        {
            Pos = new Vec2(newX, landing.Value.Y - Size.Y);
            Vel = default;
            if (_standAfterLanding)
            {
                _standAfterLanding = false;
                EnterStand(); // released from a drag -> stand where it lands
            }
            else
            {
                // Reached a surface — continue toward the target from wherever we actually landed.
                State = PetState.Walk;
                _pendingReplan = true;
            }
        }
        else
        {
            // Void recovery: a pet dragged below the ground (or released where no platform is below
            // it) would otherwise fall forever. If it drops well past the lowest surface, rescue it.
            Platform ground = world.Platforms[0];
            foreach (var p in world.Platforms) if (p.Y > ground.Y) ground = p;
            if (newFeet > ground.Y + 200)
            {
                Pos = new Vec2(ground.CenterX - Size.X / 2, ground.Y - Size.Y);
                Vel = default;
                _pendingReplan = true;
                if (_standAfterLanding) { _standAfterLanding = false; EnterStand(); }
                else State = PetState.Walk;
                return;
            }

            Pos = new Vec2(newX, Pos.Y + vy * dt);
            Vel = new Vec2(vx, vy);
        }
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
