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
    private const int TargetTries = 16;     // attempts to find a reachable random target

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
        var eligible = _graph.EligibleTargets(_cfg.RoamingHeight);
        if (eligible.Count == 0) return false;

        for (int attempt = 0; attempt < TargetTries; attempt++)
        {
            var node = _graph.Nodes[eligible[_rng.Next(eligible.Count)]];
            // Skip targets we're basically already at.
            if (Math.Abs(node.X - CenterX) < 24 && Math.Abs(node.Y - FeetY) < SupportTol) continue;

            var path = _graph.FindPath(CenterX, FeetY, _graph.NearestNode(node.X, node.Y));
            if (path is { Count: > 0 })
            {
                _path = path;
                _step = 0;
                _targetPos = new Vec2(node.X, node.Y);
                _hasTarget = true;
                _pendingReplan = false;
                return true;
            }
        }
        return false;
    }

    private void RecomputePath(World world)
    {
        _pendingReplan = false;
        if (!_hasTarget || _graph is null) { _path = null; return; }
        int goal = _graph.NearestNode(_targetPos.X, _targetPos.Y);
        _path = _graph.FindPath(CenterX, FeetY, goal);
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
            case MoveKind.ClimbDown:
                if (WalkToward(world, dt, step.X)) StartRope(world, step);
                break;
            case MoveKind.DropDown:
                if (WalkToward(world, dt, step.X))
                {
                    // Step the feet just off the source surface first, otherwise FindLanding would
                    // immediately re-detect the platform we're leaving and we'd never actually fall.
                    Pos = new Vec2(Pos.X, Pos.Y + 4.0);
                    BeginFall(); // lands on the platform below -> replan from there
                }
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
