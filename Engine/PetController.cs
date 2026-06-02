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
/// The pet's state machine and physics integration. Platform-agnostic: it only ever reads a
/// <see cref="World"/> of platforms/ladders, so it is fully unit-testable.
///
/// States: STAND (idle) / WALK / ROPE (on a ladder) / JUMP (airborne).
///
/// Movement rules:
///  - Walks left/right on platforms; pauses to idle (STAND) now and then; turns at solid ends.
///  - UP is only via ladders. When it passes close to a reachable ladder it climbs with
///    probability <c>RoamingChance</c>. A ladder is reachable if its grab point is within
///    <c>JumpHeight</c> above the platform.
///  - DOWN is via a ladder OR by walking off a cliff edge and dropping.
///  - The world is re-resolved every tick (dynamic-world rule): if the surface the pet is on
///    or the ladder it climbs disappears, it falls.
///
/// Position is the pet's top-left corner, in logical pixels.
/// </summary>
public sealed class PetController
{
    private readonly Settings _cfg;
    private readonly Random _rng;

    private const double SupportTol = 8.0;      // feet-to-platform snap tolerance
    private const double LadderXTol = 5.0;      // how near the ladder line counts as "on" it
    private const double DetachCooldown = 0.45; // seconds; suppress re-grab right after (de)taching
    private const double ReRollCooldown = 0.12; // seconds; avoid re-rolling the same encounter
    private const double IdleChancePerSecond = 0.3; // how often a walking pet pauses to idle
    private const double IdleMinSeconds = 1.0;
    private const double IdleMaxSeconds = 3.0;

    public Vec2 Pos;          // top-left, logical px
    public Vec2 Size;         // width/height, logical px
    public Vec2 Vel;          // px/second
    public PetState State { get; private set; } = PetState.Jump;
    public int Facing { get; private set; } = 1; // +1 = right, -1 = left
    public bool IsDragging { get; private set; }  // held by the cursor; physics is suspended

    private bool _spawned;
    private double _climbX;    // ladder line being climbed
    private int _climbDir;     // -1 = up (y decreasing), +1 = down
    private double _cooldown;
    private double _idleTimer;
    private bool _standAfterLanding; // after a drag-release, stand where it lands
    private double _lastRolledX = double.NaN; // ladder X whose roaming roll was just resolved

    public double FeetY => Pos.Y + Size.Y;
    public double CenterX => Pos.X + Size.X / 2;

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
        if (_cooldown > 0) _cooldown -= dt;

        switch (State)
        {
            case PetState.Stand: UpdateStand(world, dt); break;
            case PetState.Walk: UpdateWalk(world, dt); break;
            case PetState.Rope: UpdateRope(world, dt); break;
            case PetState.Jump: UpdateJump(world, dt); break;
        }
    }

    // ---------------------------------------------------------------- Drag (cursor-driven)
    /// <summary>Begin a cursor drag: suspend physics and hold the pet airborne (JUMP).</summary>
    public void BeginDrag()
    {
        IsDragging = true;
        State = PetState.Jump;
        Vel = default;
    }

    /// <summary>Move the held pet so its center sits at <paramref name="center"/> (logical px).</summary>
    public void DragTo(Vec2 center)
    {
        if (IsDragging)
            Pos = new Vec2(center.X - Size.X / 2, center.Y - Size.Y / 2);
    }

    /// <summary>Release the pet: it falls (JUMP) and stands where it lands.</summary>
    public void EndDrag()
    {
        IsDragging = false;
        State = PetState.Jump;
        Vel = default;
        _standAfterLanding = true;
    }

    /// <summary>Place the pet on the lowest (closest to bottom) platform — the taskbar.</summary>
    private void EnsureSpawn(World world)
    {
        if (_spawned) return;

        Platform ground = world.Platforms[0];
        foreach (var p in world.Platforms)
            if (p.Y > ground.Y) ground = p;

        Pos = new Vec2(ground.CenterX - Size.X / 2, ground.Y - Size.Y);
        Vel = default;
        State = PetState.Walk;
        Facing = 1;
        _spawned = true;
    }

    // ---------------------------------------------------------------- Stand (idle)
    private void UpdateStand(World world, double dt)
    {
        var support = Physics.FindSupport(world, CenterX, FeetY, SupportTol);
        if (support is null) { BeginFall(0); return; } // ground vanished beneath us
        Pos = new Vec2(Pos.X, support.Value.Y - Size.Y);

        _idleTimer -= dt;
        if (_idleTimer <= 0)
        {
            State = PetState.Walk;
            if (_rng.NextDouble() < 0.5) Facing = -Facing; // sometimes wander the other way
        }
    }

    private void EnterStand()
    {
        State = PetState.Stand;
        Vel = default;
        _idleTimer = IdleMinSeconds + _rng.NextDouble() * (IdleMaxSeconds - IdleMinSeconds);
    }

    // ---------------------------------------------------------------- Walk
    private void UpdateWalk(World world, double dt)
    {
        var support = Physics.FindSupport(world, CenterX, FeetY, SupportTol);
        if (support is null) { BeginFall(0); return; } // surface vanished
        var plat = support.Value;
        Pos = new Vec2(Pos.X, plat.Y - Size.Y); // snap feet onto the platform

        // Occasionally stop and idle.
        if (_cooldown <= 0 && _rng.NextDouble() < IdleChancePerSecond * dt)
        {
            EnterStand();
            return;
        }

        double prevCenter = CenterX;
        double nextX = Pos.X + Facing * _cfg.WalkSpeed * dt;
        double nextCenter = nextX + Size.X / 2;

        // Did we pass close to a reachable ladder this step? Maybe climb it.
        if (TryStartClimb(world, plat.Y, prevCenter, nextCenter)) return;

        // Hit a platform edge?
        if (nextCenter < plat.XStart) { ResolveEdge(world, plat.XStart); return; }
        if (nextCenter > plat.XEnd) { ResolveEdge(world, plat.XEnd); return; }

        Pos = new Vec2(nextX, Pos.Y);
    }

    /// <summary>
    /// At a platform end: step across to continuous ground if a neighbour platform continues at
    /// ~the same height (abutting same-height windows); else drop off if it's a cliff; else turn around.
    /// </summary>
    private void ResolveEdge(World world, double edgeX)
    {
        Pos = new Vec2(edgeX - Size.X / 2, Pos.Y); // center exactly on the edge

        double probeX = edgeX + Facing * (Physics.Eps + 1.0); // just past the current platform's lip
        var across = Physics.FindSupport(world, probeX, FeetY, SupportTol);
        if (across is not null)
        {
            // Continuous walkable ground (a different platform at ~same Y) -> keep walking onto it.
            Pos = new Vec2(probeX - Size.X / 2, across.Value.Y - Size.Y);
            return;
        }

        if (Physics.HasPlatformBelow(world, edgeX, FeetY))
        {
            Pos = new Vec2(Pos.X + Facing * 1.0, Pos.Y); // nudge just past the lip
            BeginFall(Facing * _cfg.WalkSpeed);          // carry walk momentum off the edge (drop down)
        }
        else
        {
            Facing = -Facing; // solid ground / screen edge -> turn around
        }
    }

    private void BeginFall(double vx)
    {
        Vel = new Vec2(vx, 0);
        State = PetState.Jump;
    }

    // ---------------------------------------------------------------- Climb decision
    private bool TryStartClimb(World world, double platformY, double prevCenter, double nextCenter)
    {
        if (_cooldown > 0) return false;

        // Forget the last rolled ladder once we've clearly walked past it.
        if (!double.IsNaN(_lastRolledX) && Math.Abs(CenterX - _lastRolledX) > LadderXTol)
            _lastRolledX = double.NaN;

        double lo = Math.Min(prevCenter, nextCenter) - Physics.Eps;
        double hi = Math.Max(prevCenter, nextCenter) + Physics.Eps;

        Ladder? chosen = null;
        int chosenDir = 0;
        double bestDist = double.MaxValue;
        foreach (var l in world.Ladders)
        {
            if (l.X < lo || l.X > hi) continue;            // not crossed this step
            if (!double.IsNaN(_lastRolledX) && Math.Abs(l.X - _lastRolledX) <= LadderXTol)
                continue;                                  // already rolled this exact ladder
            int dir = ClimbDirection(l, platformY);
            if (dir == 0) continue;                        // not reachable / nothing to climb
            double d = Math.Abs(l.X - prevCenter);
            if (d < bestDist) { bestDist = d; chosen = l; chosenDir = dir; }
        }
        if (chosen is null) return false;

        // Resolve this encounter exactly once. Remembering the ladder X (rather than a blanket
        // time cooldown) stops the same ladder being re-rolled while we sweep across it — the
        // crossing window spans a couple of ticks — without suppressing distinct nearby ladders.
        _lastRolledX = chosen.Value.X;
        if (_rng.NextDouble() * 100.0 >= _cfg.RoamingChance)
            return false; // failed the roaming roll -> keep walking past it

        Attach(chosen.Value, platformY, chosenDir);
        return true;
    }

    /// <summary>
    /// Decide which way (if any) the pet can take a ladder from a platform at <paramref name="platformY"/>:
    /// -1 climb up (preferred), +1 climb down, 0 = not reachable. Up requires the grab point to be
    /// within <c>JumpHeight</c> above the platform.
    /// </summary>
    private int ClimbDirection(Ladder l, double platformY)
    {
        double yTop = l.YTop, yBottom = l.YBottom, p = platformY;

        bool canUp;
        if (p > yBottom) canUp = (p - yBottom) <= _cfg.JumpHeight; // jump up to the ladder's bottom
        else if (p >= yTop) canUp = yTop < p - Physics.Eps;        // ladder crosses platform, more above
        else canUp = false;                                        // ladder lies below the platform

        bool canDown = p >= yTop && p <= yBottom && yBottom > p + Physics.Eps;

        if (canUp) return -1;
        if (canDown) return 1;
        return 0;
    }

    private void Attach(Ladder l, double platformY, int dir)
    {
        double feet = dir < 0 && platformY > l.YBottom
            ? l.YBottom                                  // jumped up to grab the bottom
            : Clamp(platformY, l.YTop, l.YBottom);

        _climbX = l.X;
        _climbDir = dir;
        Pos = new Vec2(l.X - Size.X / 2, feet - Size.Y);
        Vel = default;
        State = PetState.Rope;
    }

    // ---------------------------------------------------------------- Rope (climbing)
    private void UpdateRope(World world, double dt)
    {
        var seg = Physics.FindLadderAt(world, _climbX, FeetY, LadderXTol, SupportTol);
        if (seg is null) { BeginFall(0); _cooldown = DetachCooldown; return; } // ladder vanished
        var l = seg.Value;
        _climbX = l.X;

        double feet = FeetY + _climbDir * _cfg.ClimbSpeed * dt;

        if (_climbDir < 0 && feet <= l.YTop) { DetachAt(world, l.YTop, faceInward: true); return; }
        if (_climbDir > 0 && feet >= l.YBottom) { DetachAt(world, l.YBottom, faceInward: false); return; }

        Pos = new Vec2(l.X - Size.X / 2, feet - Size.Y);
    }

    private void DetachAt(World world, double feetY, bool faceInward)
    {
        Pos = new Vec2(_climbX - Size.X / 2, feetY - Size.Y);
        _cooldown = DetachCooldown;

        var support = Physics.FindSupport(world, CenterX, feetY, SupportTol);
        if (support is null) { BeginFall(0); return; } // e.g. occluded top -> fall

        var p = support.Value;
        Pos = new Vec2(Pos.X, p.Y - Size.Y);
        Vel = default;
        State = PetState.Walk;

        if (faceInward) // stepped onto a window top at its edge -> walk inward, not off it
            Facing = Math.Abs(_climbX - p.XStart) <= Math.Abs(_climbX - p.XEnd) ? 1 : -1;
    }

    // ---------------------------------------------------------------- Jump (airborne)
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
                State = PetState.Walk;
                _cooldown = ReRollCooldown;
            }
        }
        else
        {
            Pos = new Vec2(newX, Pos.Y + vy * dt);
            Vel = new Vec2(vx, vy);
        }
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
