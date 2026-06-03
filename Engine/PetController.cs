using System;
using System.Linq;

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
/// (<see cref="MapGraph"/>): when idle it rolls <c>RoamingLevel</c> to decide whether to wander —
/// if so it picks a random reachable target, plans a path of walk / climb / drop moves, and follows
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
    private const double LadderJumpRise = 70;  // how high the jump-onto-a-ladder arc rises to grab
    private const double MinLadderJump = 16;   // below this climb, just step onto the rope (no arc)
    private const double PanicMinSeconds = 0.10; // dragged-in-air panic: shortest hold before the next random turn
    private const double PanicMaxSeconds = 0.32; // ...and the longest, so the flailing looks erratic, not metronomic
    private const double PanicFlipChance = 0.75; // odds each turn actually flips facing (vs. re-picking the same way)

    public Vec2 Pos;          // top-left, logical px
    public Vec2 Size;         // width/height, logical px
    public Vec2 Vel;          // px/second
    public PetState State { get; private set; } = PetState.Jump;
    public int Facing { get; private set; } = 1; // +1 = right, -1 = left
    public bool IsDragging { get; private set; }

    /// <summary>The lowest feet-Y (logical px) the pet will roam to: targets on platforms below this
    /// are skipped, keeping the (tall) pet clear of the screen bottom. Set from the overlay;
    /// +infinity means no limit. The ground stays reachable for spawning/landing regardless.</summary>
    public double RoamMaxY { get; set; } = double.PositiveInfinity;

    // Navigation
    private MapGraph? _graph;
    private World? _graphWorld;
    private double _graphJumpHeight; // JumpHeight the cached graph was built with (rebuild on change)
    private List<PathStep>? _path;
    private int _step;
    private Vec2 _targetPos;
    private bool _hasTarget;
    private bool _pendingReplan;

    private bool _spawned;
    private double _idleTimer;
    private bool _standAfterLanding; // after a drag-release, stand where it lands
    private double _panicTimer;       // countdown to the next random turn while being dragged ("panic" flailing)

    // Rope execution
    private double _ropeX;
    private double _ropeTargetY;
    private int _ropeDir; // -1 up, +1 down

    // Jump-onto-a-ladder execution: while airborne, latch onto the ladder when the arc's apex
    // (where the pet's x meets the ladder line) is reached.
    private bool _grabbingLadder;
    private double _grabLadderX;

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
        // Held by the cursor: DragTo drives position; here the pet just flails in panic (rapidly
        // turning left/right). Run this before the empty-world guard so it flails even mid-screen
        // with no platforms in view, and skip all navigation/physics while held.
        if (IsDragging) { UpdatePanic(dt); return; }
        if (world.Platforms.Count == 0) return;

        EnsureSpawn(world);

        // Rebuild the nav graph only when the window geometry actually changed (or JumpHeight was
        // edited live, which changes which gaps are jumpable). A fresh World instance is produced
        // every poll even when nothing moved; rebuilding + replanning every poll would reset the
        // route to step 0 mid-walk and make the pet stutter / shuttle in place.
        if (!ReferenceEquals(world, _graphWorld))
        {
            if (_graph is null || _graphWorld is null
                || _cfg.JumpHeight != _graphJumpHeight
                || !SameGeometry(_graphWorld, world))
            {
                _graph = MapGraph.Build(world, _cfg.JumpHeight);
                _graphJumpHeight = _cfg.JumpHeight;
                _pendingReplan = true;
            }
            _graphWorld = world;
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
        _grabbingLadder = false;
        _panicTimer = 0; // startle-turn on the very next tick
    }

    /// <summary>
    /// While held in the air, the pet flails — turning left/right at random short intervals so it
    /// looks panicked. Both the timing and whether a turn actually flips are randomized, so the motion
    /// reads as frantic rather than a mechanical metronome. Only <see cref="Facing"/> changes here; the
    /// cursor still drives position via <see cref="DragTo"/>.
    /// </summary>
    private void UpdatePanic(double dt)
    {
        _panicTimer -= dt;
        if (_panicTimer > 0) return;
        if (_rng.NextDouble() < PanicFlipChance) Facing = -Facing;
        _panicTimer = PanicMinSeconds + _rng.NextDouble() * (PanicMaxSeconds - PanicMinSeconds);
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
        _grabbingLadder = false;
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
        _grabbingLadder = false;
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
            // Decide whether to wander. RoamingLevel is the restlessness knob; the ease-in curve makes
            // low levels strongly prefer staying put (level 0 never roams, ~10 ≈ 1%, ~25 ≈ 6% per check)
            // while high levels roam eagerly (100 = every check), which reads more naturally than linear.
            if (_rng.NextDouble() >= MoveChance())
                EnterStand();          // decided to stay put; re-idle for a full spell before re-deciding
            else if (PickTarget(world))
                State = PetState.Walk; // off it goes
            else
                _idleTimer = 0.5;      // wanted to move but nothing reachable yet; retry shortly
        }
    }

    /// <summary>Probability (0..1) of starting a new wander on a given idle check, from RoamingLevel.</summary>
    private double MoveChance()
    {
        double t = Math.Clamp(_cfg.RoamingLevel, 0, 100) / 100.0;
        return t * t; // ease-in: 0 never roams, 100 always; low levels stay put far more than linear
    }

    // ---------------------------------------------------------------- Target selection
    private bool PickTarget(World world)
    {
        if (_graph is null) return false;

        // Cap the roam range: skip platforms below RoamMaxY so the pet (which is drawn tall, above
        // its feet) stays clear of the screen bottom. The ground itself stays in the world for
        // spawning/landing — this only limits where the pet chooses to wander.
        var all = _graph.RoamablePlatforms();
        var plats = new List<int>(all.Count);
        foreach (int i in all)
            if (world.Platforms[i].Y <= RoamMaxY) plats.Add(i);
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

        // Already standing on the target's platform? Walk straight to it — a single platform is one
        // contiguous walkable segment. Routing through a graph node here could pick an edge node on
        // the FAR side of the target, and (with periodic replanning resetting the step) leave the pet
        // shuttling to the platform edge instead of stopping at the target.
        if (pi >= 0 && _graph.PlatformAt(CenterX, FeetY) == pi)
            return new List<PathStep> { new(MoveKind.Walk, target.X, target.Y, -1) };

        // Target on a known platform: enter at whichever node minimizes (reach cost + walk to target)
        // and finish by walking to the exact target, so the pet never overshoots to a far edge.
        if (pi >= 0)
            return _graph.FindPathToTarget(CenterX, FeetY, pi, target.X, target.Y);

        // Target not resolvable to a platform: fall back to the nearest node, then walk to it.
        int goal = _graph.NearestNode(target.X, target.Y);
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

        // Approaching a ladder we're about to climb: run up and jump onto it in an arc so the
        // apex (where horizontal motion is fastest and vertical motion stalls) meets the ladder
        // line. Only the FIRST entry onto a rope (a Walk immediately before a ClimbUp) does this;
        // continuing up a multi-stop ladder grabs straight on.
        if (step.Kind == MoveKind.Walk
            && _step + 1 < _path.Count
            && _path[_step + 1].Kind == MoveKind.ClimbUp
            && ApproachLadderJump(world, dt, _path[_step + 1]))
            return;

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

    /// <summary>
    /// While walking toward a ladder we intend to climb, run up and — once within the launch
    /// distance — jump in an arc whose apex lands exactly on the ladder line, then grab on mid-air.
    /// The horizontal launch speed is the walk speed (so momentum carries through), and the launch
    /// point sits ahead of the ladder by exactly the horizontal distance covered during the ascent,
    /// so x reaches the ladder as vertical velocity stalls (the peak). Returns true if it handled
    /// the tick (walking up or launching); false to let the caller fall back to a plain grab.
    /// </summary>
    private bool ApproachLadderJump(World world, double dt, PathStep climb)
    {
        if (climb.LadderIndex < 0 || climb.LadderIndex >= world.Ladders.Count) return false;

        var support = Physics.FindSupport(world, CenterX, FeetY, SupportTol);
        if (support is null) { BeginFall(); return true; } // lost the ground
        Pos = new Vec2(Pos.X, support.Value.Y - Size.Y);

        var l = world.Ladders[climb.LadderIndex];
        double py = FeetY;
        double g = Math.Max(1.0, _cfg.Gravity);

        // The grab point (the arc's apex) on the ladder: rise LadderJumpRise, but never above the
        // ladder top and never past the climb target (climb.Y), so we don't overshoot.
        double topBound = Math.Max(l.YTop, climb.Y);
        double grabY = Clamp(py - LadderJumpRise, topBound, l.YBottom);
        double rise = py - grabY;
        if (rise < MinLadderJump) return false; // climb too short to arc; let the plain grab handle it

        double v0 = Math.Sqrt(2 * g * rise); // upward launch speed (apex after t = v0/g)
        double tApex = v0 / g;
        double launchDist = _cfg.WalkSpeed * tApex; // run-up distance: vx == walk speed at the apex

        double dist = l.X - CenterX;
        int dir = dist >= 0 ? 1 : -1;
        if (Math.Abs(dist) > launchDist + ArriveTol)
        {
            // Still approaching: keep running toward the ladder.
            Facing = dir;
            Pos = new Vec2(Pos.X + dir * _cfg.WalkSpeed * dt, Pos.Y);
            return true;
        }

        // Within range: launch. vx is sized so x reaches the ladder exactly at the apex.
        double vx = tApex > 1e-3 ? dist / tApex : 0;
        Vel = new Vec2(vx, -v0);
        if (Math.Abs(vx) > 1e-3) Facing = vx > 0 ? 1 : -1; // face the way we leap (else keep facing)
        State = PetState.Jump;
        _grabbingLadder = true;
        _grabLadderX = l.X;
        _ropeTargetY = climb.Y;
        _step += 1; // consume the Walk step; the ClimbUp step finishes when the rope reaches the top
        return true;
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
        _grabbingLadder = false;
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
        _grabbingLadder = false;
        Vel = new Vec2(vx, -v0);
        if (Math.Abs(vx) > 1e-3) Facing = vx > 0 ? 1 : -1; // face the way we leap
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
        _grabbingLadder = false;
        Vel = new Vec2(vx, 0);
        if (Math.Abs(vx) > 1e-3) Facing = vx > 0 ? 1 : -1; // face the way we drop
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

        // Jumping onto a ladder: when the arc's apex reaches the ladder line, latch on.
        if (_grabbingLadder)
        {
            bool reachedX = (vx > 0 && newCenter >= _grabLadderX)
                         || (vx < 0 && newCenter <= _grabLadderX)
                         || (Math.Abs(vx) <= 1e-3 && vy >= 0);
            if (reachedX)
            {
                _grabbingLadder = false;
                var seg = Physics.FindLadderAt(world, _grabLadderX, newFeet, LadderXTol, SupportTol * 2, -1);
                if (seg is Ladder l)
                {
                    double grab = Clamp(newFeet, l.YTop, l.YBottom);
                    _ropeX = l.X;
                    _ropeDir = _ropeTargetY < grab ? -1 : 1;
                    Pos = new Vec2(l.X - Size.X / 2, grab - Size.Y);
                    Vel = default;
                    State = PetState.Rope;
                    return;
                }
                // The ladder vanished mid-jump (world changed): fall and replan from where we land.
                _pendingReplan = true;
            }
        }

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

    /// <summary>Test seam (destructive: rebuilds the graph and teleports Pos — use only on a
    /// throwaway controller): build the graph for <paramref name="world"/> and plan a route from the
    /// feet-center <paramref name="fromCenterFeet"/> to <paramref name="target"/> (see NavTest).</summary>
    internal List<PathStep>? PlanForTest(World world, Vec2 fromCenterFeet, Vec2 target)
    {
        _graph = MapGraph.Build(world, _cfg.JumpHeight);
        Pos = new Vec2(fromCenterFeet.X - Size.X / 2, fromCenterFeet.Y - Size.Y);
        return PlanTo(target);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>
    /// True if two worlds describe the same platforms and ladders (value equality), so the nav graph
    /// can be reused. Order-INSENSITIVE: window geometry is emitted in Z-order, which reshuffles on a
    /// mere focus change with nothing actually moving — comparing in order would then needlessly
    /// rebuild + replan (resetting the route). Overlapping windows that swap Z-order genuinely change
    /// their visible segments, so their sorted values still differ and a rebuild correctly happens.
    /// </summary>
    internal static bool SameGeometry(World a, World b)
    {
        if (a.Platforms.Count != b.Platforms.Count || a.Ladders.Count != b.Ladders.Count) return false;

        var pa = a.Platforms.OrderBy(p => p.Y).ThenBy(p => p.XStart).ThenBy(p => p.XEnd);
        var pb = b.Platforms.OrderBy(p => p.Y).ThenBy(p => p.XStart).ThenBy(p => p.XEnd);
        if (!pa.SequenceEqual(pb)) return false;

        var la = a.Ladders.OrderBy(l => l.X).ThenBy(l => l.YTop).ThenBy(l => l.YBottom);
        var lb = b.Ladders.OrderBy(l => l.X).ThenBy(l => l.YTop).ThenBy(l => l.YBottom);
        return la.SequenceEqual(lb);
    }
}
