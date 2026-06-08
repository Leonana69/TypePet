using System.IO;
using System.Linq;
using System.Text;

namespace MaplePet.Engine;

/// <summary>
/// Dev-only deterministic checks for the roaming path planner against synthetic worlds (no OS, no
/// Avalonia). Triggered by <c>--nav-test &lt;outfile&gt;</c>; writes a human-readable report plus a
/// PASS/FAIL summary so the path-generation fixes can be verified without reproducing a live desktop.
/// </summary>
public static class NavTest
{
    public static void Run(string outFile)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
        }

        static string Fmt(System.Collections.Generic.List<PathStep>? path) =>
            path is null ? "<null>" : string.Join(" -> ", path.ConvertAll(s => $"{s.Kind}({s.X:0.#},{s.Y:0.#})"));

        var cfg = new Settings();

        // ---- Scenario A: target on the SAME platform, to the LEFT of the pet (bug 1/2). ----
        // One wide platform; pet at x=300 walking-distance from a target at x=100. Expect a single
        // direct Walk to the target, NOT a detour through the left-edge node at x=0.
        {
            var world = new World(new[] { new Platform(500, 0, 400) }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38));
            var path = pet.PlanForTest(world, new Vec2(300, 500), new Vec2(100, 500));
            bool ok = path is { Count: 1 } && path[0].Kind == MoveKind.Walk && System.Math.Abs(path[0].X - 100) < 0.5;
            Check("same-platform target is a single direct walk", ok, Fmt(path));
        }

        // ---- Scenario B: same platform, target to the RIGHT. Also a single direct walk. ----
        {
            var world = new World(new[] { new Platform(500, 0, 400) }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38));
            var path = pet.PlanForTest(world, new Vec2(100, 500), new Vec2(320, 500));
            bool ok = path is { Count: 1 } && path[0].Kind == MoveKind.Walk && System.Math.Abs(path[0].X - 320) < 0.5;
            Check("same-platform target (right) is a single direct walk", ok, Fmt(path));
        }

        // ---- Scenario D: down-jump goes STRAIGHT DOWN (bug 3). Two stacked, fully overlapping ----
        // platforms; the only way down is a DropDown. Expect launchX == landX (no lean).
        {
            var top = new Platform(300, 0, 200);
            var bottom = new Platform(500, 0, 200);
            var world = new World(new[] { top, bottom }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38));
            var path = pet.PlanForTest(world, new Vec2(100, 300), new Vec2(150, 500));
            int di = path?.FindIndex(s => s.Kind == MoveKind.DropDown) ?? -1;
            bool hasDrop = di >= 0;
            // The step before the drop is the walk to the launch x; the drop step carries the land x.
            bool straight = hasDrop && di > 0 && System.Math.Abs(path![di].X - path[di - 1].X) < 0.5;
            Check("down-jump is present", hasDrop, Fmt(path));
            Check("down-jump is straight down (launchX == landX)", !hasDrop || straight,
                hasDrop ? $"launchX={path![di - 1].X:0.#} landX={path[di].X:0.#}" : "no drop");
        }

        // ---- Scenario C: cross-platform via a ladder still plans (sanity, no crash). ----
        {
            var lower = new Platform(500, 0, 300);
            var upper = new Platform(300, 100, 400);
            var ladder = new Ladder(120, 300, 500); // connects lower up to upper
            var world = new World(new[] { lower, upper }, new[] { ladder });
            var pet = new PetController(cfg, new Vec2(30, 38));
            var path = pet.PlanForTest(world, new Vec2(50, 500), new Vec2(350, 300));
            bool ok = path is { Count: > 0 } && path[^1].Kind == MoveKind.Walk && System.Math.Abs(path[^1].X - 350) < 0.5;
            Check("cross-platform path ends at the exact target x", ok, Fmt(path));

            // No overshoot: once on the upper platform (Y==300) the pet must not walk past the
            // target (x=350) to a far edge (x=400) and come back.
            bool noOvershoot = path is not null &&
                path.TrueForAll(s => System.Math.Abs(s.Y - 300) > 0.5 || s.X <= 350 + 0.5);
            Check("cross-platform path does not overshoot the target", noOvershoot, Fmt(path));
        }

        // ---- Scenario E: occluded drop center. A small platform M sits directly under the overlap ----
        // center of A and the floor B. EVERY down-jump must be straight (launchX == landX) so the plan
        // matches physics — no leaning arc that claims to reach B but actually arcs through M. The pet
        // legitimately steps DOWN onto M then off it; what matters is it ends on the floor at the target
        // and never leans (the old bug planned a single A->B lean that physically landed on M).
        {
            var a = new Platform(200, 0, 200);
            var m = new Platform(350, 90, 110); // occludes the column straight below A's center
            var b = new Platform(500, 0, 200);
            var world = new World(new[] { a, m, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38));
            var path = pet.PlanForTest(world, new Vec2(100, 200), new Vec2(150, 500));

            bool allDropsStraight = path is not null;
            if (path is not null)
                for (int i = 0; i < path.Count; i++)
                    if (path[i].Kind == MoveKind.DropDown && (i == 0 || System.Math.Abs(path[i].X - path[i - 1].X) > 0.5))
                        allDropsStraight = false;
            bool reachedFloorTarget = path is { Count: > 0 } && path[^1].Kind == MoveKind.Walk
                && System.Math.Abs(path[^1].X - 150) < 0.5 && System.Math.Abs(path[^1].Y - 500) < 0.5;

            Check("occluded drop uses only straight drops (no lean through the occluder)", allDropsStraight, Fmt(path));
            Check("occluded drop ends on the floor at the target", reachedFloorTarget, Fmt(path));
        }

        // ---- Scenario F: SameGeometry is order-insensitive (window Z-order churn must not force a ----
        // rebuild). Same platforms/ladders in a different list order compare equal; a real change does not.
        {
            var p0 = new Platform(500, 0, 200);
            var p1 = new Platform(300, 250, 400);
            var l0 = new Ladder(250, 300, 500);
            var w1 = new World(new[] { p0, p1 }, new[] { l0 });
            var w2 = new World(new[] { p1, p0 }, new[] { l0 });              // reordered, same geometry
            var w3 = new World(new[] { p0, p1 with { XEnd = 410 } }, new[] { l0 }); // genuinely different
            Check("SameGeometry ignores list order", PetController.SameGeometry(w1, w2), "reordered == equal");
            Check("SameGeometry detects a real change", !PetController.SameGeometry(w1, w3), "moved edge != equal");
        }

        // ---- Scenario G: WorldModel clips geometry to the screen (feature 1). A window straddles ----
        // the left edge; its off-screen part must be removed so the pet can't target/walk there.
        {
            var screen = new Rect(0, 0, 800, 600);
            var win = new Rect(-50, 200, 200, 300); // spans x:-50..150 (left half off-screen), y:200..500
            var geo = new WorldGeometry(new[] { win }, new Rect(0, 590, 800, 10), TaskbarEdge.Bottom);
            var world = WorldModel.Build(geo, screen);

            bool allOnScreen = true;
            foreach (var p in world.Platforms)
                if (p.XStart < screen.Left - 0.5 || p.XEnd > screen.Right + 0.5
                    || p.Y < screen.Top - 0.5 || p.Y > screen.Bottom + 0.5) allOnScreen = false;
            foreach (var l in world.Ladders)
                if (l.X < screen.Left - 0.5 || l.X > screen.Right + 0.5
                    || l.YTop < screen.Top - 0.5 || l.YBottom > screen.Bottom + 0.5) allOnScreen = false;

            Check("world geometry is clipped to the screen", allOnScreen && world.Platforms.Count > 0,
                $"platforms={world.Platforms.Count} ladders={world.Ladders.Count}");
        }

        // ---- Scenario H: a window's BOTTOM edge is a platform too, not just its top. A lone, ----
        // unoccluded window must yield walkable surfaces at BOTH w.Top and w.Bottom spanning its width.
        {
            var screen = new Rect(0, 0, 800, 600);
            var win = new Rect(100, 100, 200, 150); // x:100..300, y:100..250 (top=100, bottom=250)
            var geo = new WorldGeometry(new[] { win }, new Rect(0, 590, 800, 10), TaskbarEdge.Bottom);
            var world = WorldModel.Build(geo, screen);

            static bool HasEdge(World w, double y, double xs, double xe) =>
                w.Platforms.Any(p => System.Math.Abs(p.Y - y) < 0.5
                    && System.Math.Abs(p.XStart - xs) < 0.5 && System.Math.Abs(p.XEnd - xe) < 0.5);

            Check("window top edge is a platform", HasEdge(world, 100, 100, 300), $"platforms={world.Platforms.Count}");
            Check("window bottom edge is a platform", HasEdge(world, 250, 100, 300), $"platforms={world.Platforms.Count}");
        }

        // ---- Scenario I: the bottom edge is occluded by a window IN FRONT, exactly like the top ----
        // edge. A front window covers the middle of a back window's bottom edge, so only the two
        // flanking segments survive — and nothing spans the hidden middle.
        {
            var screen = new Rect(0, 0, 800, 600);
            var front = new Rect(250, 250, 100, 200); // x:250..350, y:250..450 — covers back.Bottom (y=300)
            var back = new Rect(100, 100, 300, 200);  // x:100..400, y:100..300 (bottom edge y=300)
            // Z-order: front first (index 0), back second (index 1).
            var geo = new WorldGeometry(new[] { front, back }, new Rect(0, 590, 800, 10), TaskbarEdge.Bottom);
            var world = WorldModel.Build(geo, screen);

            var bottomSegs = world.Platforms
                .Where(p => System.Math.Abs(p.Y - 300) < 0.5)
                .OrderBy(p => p.XStart)
                .ToList();
            bool twoFlanks = bottomSegs.Count == 2
                && System.Math.Abs(bottomSegs[0].XStart - 100) < 0.5 && System.Math.Abs(bottomSegs[0].XEnd - 250) < 0.5
                && System.Math.Abs(bottomSegs[1].XStart - 350) < 0.5 && System.Math.Abs(bottomSegs[1].XEnd - 400) < 0.5;
            bool nothingSpansHole = !bottomSegs.Any(p => p.XStart < 250 - 0.5 && p.XEnd > 350 + 0.5);

            Check("occluded bottom edge yields only its two visible segments", twoFlanks,
                string.Join(", ", bottomSegs.ConvertAll(p => $"[{p.XStart:0.#}..{p.XEnd:0.#}]")));
            Check("nothing spans the occluded bottom-edge middle", nothingSpansHole, $"segments={bottomSegs.Count}");
        }

        // ---- Scenario J: the TASKBAR occludes a window's bottom edge, exactly like the top edge. A ----
        // window whose lower border falls inside the docked taskbar's band emits NO bottom platform
        // there (it would only duplicate the taskbar ground); its top edge above the band survives.
        {
            var screen = new Rect(0, 0, 800, 600);
            var win = new Rect(100, 400, 200, 180);     // x:100..300, y:400..580
            var taskbar = new Rect(0, 560, 800, 40);    // docked bottom, band y:560..600 — covers y=580
            var geo = new WorldGeometry(new[] { win }, taskbar, TaskbarEdge.Bottom);
            var world = WorldModel.Build(geo, screen);

            bool topSurvives = world.Platforms.Any(p => System.Math.Abs(p.Y - 400) < 0.5
                && System.Math.Abs(p.XStart - 100) < 0.5 && System.Math.Abs(p.XEnd - 300) < 0.5);
            bool bottomHidden = !world.Platforms.Any(p => System.Math.Abs(p.Y - 580) < 0.5);
            Check("window top edge survives above the taskbar", topSurvives, $"platforms={world.Platforms.Count}");
            Check("taskbar occludes the window bottom edge", bottomHidden, $"platforms={world.Platforms.Count}");
        }

        // ---- Scenario K: a window extending BELOW the screen has its off-screen bottom edge clipped ----
        // away (ClipPlatforms drops any platform past screen.Bottom), while its on-screen top edge stays.
        {
            var screen = new Rect(0, 0, 800, 600);
            var win = new Rect(100, 400, 200, 300);                   // y:400..700 — bottom=700 is off-screen
            var geo = new WorldGeometry(new[] { win }, new Rect(0, 0, 0, 0), TaskbarEdge.Bottom); // no taskbar
            var world = WorldModel.Build(geo, screen);

            bool topKept = world.Platforms.Any(p => System.Math.Abs(p.Y - 400) < 0.5);
            bool noOffScreen = !world.Platforms.Any(p => p.Y > screen.Bottom + 0.5);
            Check("off-screen bottom edge is clipped away", topKept && noOffScreen, $"platforms={world.Platforms.Count}");
        }

        // ---- Scenario L: a bottom-edge fragment thinner than MinSegment (4px) is discarded. A front ----
        // window covers all but a 3px sliver of the back window's bottom edge, so no bottom platform
        // survives at that Y — the same sliver rule already applied to top/side edges.
        {
            var screen = new Rect(0, 0, 800, 600);
            var front = new Rect(103, 250, 400, 100);   // x:103..503, y:250..350 — hides back.Bottom (y=300) from x=103
            var back = new Rect(100, 100, 300, 200);    // x:100..400, y:100..300 — only a 3px flank [100..103] is left
            var geo = new WorldGeometry(new[] { front, back }, new Rect(0, 0, 0, 0), TaskbarEdge.Bottom);
            var world = WorldModel.Build(geo, screen);

            int y300 = world.Platforms.Count(p => System.Math.Abs(p.Y - 300) < 0.5);
            Check("sub-MinSegment bottom-edge sliver is discarded", y300 == 0, $"y300 platforms={y300}");
        }

        // ===== Gap-jump (leap across a detached platform) — END-TO-END runtime checks. These DRIVE =====
        // the real physics tick loop (not just the planner), because the gap-jump bugs live in the
        // launch/landing execution: re-landing on the launch lip, sailing into the void, etc.
        double simDt = 1.0 / cfg.TargetFps;

        // ---- Scenario M: same-height leap across a clear gap, executed. The pet must walk to the lip,
        // leap, and end standing on the far platform at the target — with a single launch (no stutter).
        {
            var a = new Platform(300, 0, 150);
            var b = new Platform(300, 230, 380); // 80px gap, same height
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 1);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(75, 300), new Vec2(300, 300), simDt, 600);
            Check("same-height leap reaches the far platform", atTarget && plat == 1, $"plat={plat} atTarget={atTarget} launches={launches}");
            Check("same-height leap doesn't stutter at the lip", launches <= 2, $"launches={launches}");
        }

        // ---- Scenario N: JUMP DOWN A CLIFF across a narrow gap with a steep drop — the headline case
        // AND the regression guard for the run-off launch re-landing on its own lip. Must reach B with
        // a single clean leap, not jitter at the edge.
        {
            var a = new Platform(300, 0, 150);
            var b = new Platform(540, 160, 400); // 10px gap, 240px drop -> run-off launch (LaunchVy=0)
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 2);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(75, 300), new Vec2(280, 540), simDt, 600);
            Check("cliff run-off leap reaches the lower platform", atTarget && plat == 1, $"plat={plat} atTarget={atTarget} launches={launches}");
            Check("cliff run-off leap does not re-land on the launch lip (no soft-lock)", launches <= 2, $"launches={launches}");
        }

        // ---- Scenario O: a gap too wide to clear is not offered, so the pet stays safely put rather
        // than leaping into the void.
        {
            var a = new Platform(300, 0, 150);
            var b = new Platform(300, 420, 560); // 270px gap — unjumpable
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 3);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(75, 300), new Vec2(480, 300), simDt, 300);
            Check("too-wide gap: pet stays put, never leaps", !atTarget && plat == 0 && launches == 0, $"plat={plat} atTarget={atTarget} launches={launches}");
        }

        // ---- Scenario P: an UPWARD leap onto a higher detached platform near the jump-height limit.
        // The ideal-parabola plan would graze the apex and the real (Euler) arc would sail into the
        // void and soft-loop; the executor-faithful reachability sim must reject it, so the pet ends on
        // a real platform without an endless leap/void/teleport cycle.
        {
            var a = new Platform(281, 74, 220);
            var b = new Platform(150, 265, 349); // 45px gap, 131px HIGHER (near the 150 jump limit)
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 4);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(140, 281), new Vec2(300, 150), simDt, 600);
            Check("upward near-apex leap never voids/soft-loops (ends on a platform)", plat >= 0 && launches <= 3, $"plat={plat} atTarget={atTarget} launches={launches}");
        }

        // ---- Scenario Q: a comfortably-reachable UPWARD leap onto a higher detached platform MUST be
        // taken (regression guard: the reachability sim's flight-time bound must follow the arc all the
        // way to its DESCENDING landing, not cut off at the ascending crossing, or higher platforms
        // become silently unreachable).
        {
            var a = new Platform(400, 50, 300);
            var b = new Platform(370, 303, 503); // 3px gap, 30px higher — well within a run-up leap
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 5);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(150, 400), new Vec2(400, 370), simDt, 600);
            Check("reachable upward leap is taken (not over-rejected)", atTarget && plat == 1, $"plat={plat} atTarget={atTarget} launches={launches}");
        }

        // ---- Scenario R: WALK OFF A CLIFF EDGE onto an adjacent lower platform. A and B abut exactly
        // at the edge (no x-overlap to drop straight through, no gap to leap), so ONLY an edge-drop
        // connects them — the pet keeps walking right to the lip and steps off into the fall.
        {
            var a = new Platform(300, 0, 200);
            var b = new Platform(500, 200, 600); // abuts A's right edge at x=200, 200px lower
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 6);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(80, 300), new Vec2(400, 500), simDt, 600);
            Check("walk off a cliff edge reaches the lower platform", atTarget && plat == 1, $"plat={plat} atTarget={atTarget} launches={launches}");
            Check("cliff walk-off is a single step-off (no stutter)", launches <= 2, $"launches={launches}");
        }

        // ---- Scenario S: a same-height neighbour AT the edge is walked across, not dropped through —
        // the pet only steps off where the ledge truly ends. A->C is level and abutting (walk-across,
        // no edge-drop), and C drops to a lower B past C's far edge.
        {
            var a = new Platform(300, 0, 200);
            var c = new Platform(300, 200, 400); // same height, abuts A -> walk across, NOT an edge-drop
            var b = new Platform(500, 400, 600); // lower, past C's right edge -> edge-drop from C
            var world = new World(new[] { a, c, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 7);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(80, 300), new Vec2(500, 500), simDt, 600);
            Check("walk across a level seam, then edge-drop off the true ledge", atTarget && plat == 2, $"plat={plat} atTarget={atTarget} launches={launches}");
        }

        // ---- Scenario T: a near-jump-limit GAP JUMP must not soft-lock. The planner admits it from
        // the exact edge; the executor stops within ArriveTol of the edge, so it must snap back to that
        // edge before re-solving — else the hair's shortfall tips the leap over the height cap and it
        // busy-replans the same edge forever.
        {
            var a = new Platform(238, 45, 109);
            var b = new Platform(332, 221, 356); // 112px gap, 94px lower — v0 sits right at the jump limit
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 8);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(70, 238), new Vec2(300, 332), simDt, 800);
            Check("near-jump-limit gap-jump reaches the target (no lip-precision soft-lock)", atTarget && plat == 1, $"plat={plat} atTarget={atTarget} launches={launches}");
        }

        // ---- Scenario U: the cliff walk-off must work at a HIGH frame rate too. At >=180fps the
        // per-frame advance is <= the lip tolerance, so launching AT the bare edge would re-detect it
        // and stall; stepping just past the lip keeps the edge-drop emitted AND stutter-free.
        {
            var hi = new Settings { TargetFps = 240 };
            double hiDt = 1.0 / hi.TargetFps;
            var a = new Platform(300, 0, 200);
            var b = new Platform(500, 200, 600);
            var world = new World(new[] { a, b }, System.Array.Empty<Ladder>());
            var pet = new PetController(hi, new Vec2(30, 38), seed: 9);
            var (plat, atTarget, launches) = pet.SimulateForTest(world, new Vec2(80, 300), new Vec2(400, 500), hiDt, 3000);
            Check("cliff walk-off works at high frame rate (lip cleared frame-rate-independently)",
                atTarget && plat == 1 && launches <= 2, $"plat={plat} atTarget={atTarget} launches={launches}");
        }

        // ===== Commanded JUMP / FLY (do_action "jump"/"fly") — END-TO-END runtime checks. =====
        // Jump arcs onto a platform directly overhead within JumpHeight, else hops in place and returns.
        // Fly glides onto the platform overhead at ANY height, else floats up a little and returns. The
        // head-clearance ceiling (RoamMinY) excludes a maximized window's top edge from both.

        // ---- Scenario V: jump onto a platform directly above WITHIN jump height (lands on it). ----
        {
            var floor = new Platform(400, 0, 200);
            var above = new Platform(300, 0, 200); // 100px up (< JumpHeight 150), overlaps x
            var world = new World(new[] { floor, above }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 10);
            var (feetY, cx, standing, started) = pet.SimulateActionForTest(world, new Vec2(100, 400), fly: false, simDt, 1200);
            Check("jump within reach lands on the platform above", started && standing
                && System.Math.Abs(feetY - 300) < 1.0 && System.Math.Abs(cx - 100) < 2.0,
                $"feetY={feetY:0.#} cx={cx:0.#} standing={standing}");
        }

        // ---- Scenario W: platform overhead is BEYOND jump height -> hop in place, back to the start. ----
        {
            var floor = new Platform(500, 0, 200);
            var tooHigh = new Platform(300, 0, 200); // 200px up (> JumpHeight 150)
            var world = new World(new[] { floor, tooHigh }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 11);
            var (feetY, cx, standing, _) = pet.SimulateActionForTest(world, new Vec2(100, 500), fly: false, simDt, 1200);
            Check("jump out of reach hops in place and returns to the same spot", standing
                && System.Math.Abs(feetY - 500) < 1.0 && System.Math.Abs(cx - 100) < 2.0,
                $"feetY={feetY:0.#} cx={cx:0.#}");
        }

        // ---- Scenario X: platform overhead is OFFSET in x (not above the pet) -> jump hops in place. ----
        {
            var floor = new Platform(500, 0, 200);
            var offset = new Platform(420, 300, 500); // higher but not over x=100
            var world = new World(new[] { floor, offset }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 12);
            var (feetY, cx, standing, _) = pet.SimulateActionForTest(world, new Vec2(100, 500), fly: false, simDt, 1200);
            Check("jump ignores a platform not directly overhead (hops in place)", standing
                && System.Math.Abs(feetY - 500) < 1.0 && System.Math.Abs(cx - 100) < 2.0,
                $"feetY={feetY:0.#} cx={cx:0.#}");
        }

        // ---- Scenario Y: FLY reaches a platform overhead FAR beyond jump height (no height limit). ----
        {
            var floor = new Platform(500, 0, 200);
            var high = new Platform(200, 0, 200); // 300px up — unreachable by jump, fine for fly
            var world = new World(new[] { floor, high }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 13);
            var (feetY, cx, standing, started) = pet.SimulateActionForTest(world, new Vec2(100, 500), fly: true, simDt, 1200);
            Check("fly lands on the platform above at any height", started && standing
                && System.Math.Abs(feetY - 200) < 1.0 && System.Math.Abs(cx - 100) < 2.0,
                $"feetY={feetY:0.#} cx={cx:0.#}");
        }

        // ---- Scenario Z: FLY with nothing overhead -> float up a little and drift back to the start. ----
        {
            var floor = new Platform(500, 0, 200);
            var world = new World(new[] { floor }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 14);
            var (feetY, cx, standing, _) = pet.SimulateActionForTest(world, new Vec2(100, 500), fly: true, simDt, 1200);
            Check("fly with no platform above returns to the same spot", standing
                && System.Math.Abs(feetY - 500) < 1.0 && System.Math.Abs(cx - 100) < 2.0,
                $"feetY={feetY:0.#} cx={cx:0.#}");
        }

        // ---- Scenario AA: a maximized window's top edge (above the head-clearance ceiling) is NOT a ----
        // fly target — the pet would vanish above it; instead it floats up a little and returns.
        {
            var floor = new Platform(500, 0, 800);
            var maxTop = new Platform(40, 0, 800); // a maximized window's top edge, near the screen top
            var world = new World(new[] { floor, maxTop }, System.Array.Empty<Ladder>());
            var pet = new PetController(cfg, new Vec2(30, 38), seed: 15) { RoamMinY = 150 };
            var (feetY, cx, standing, _) = pet.SimulateActionForTest(world, new Vec2(100, 500), fly: true, simDt, 1200);
            Check("fly ignores a maximized-window top edge (stays on screen, returns)", standing
                && System.Math.Abs(feetY - 500) < 1.0 && System.Math.Abs(cx - 100) < 2.0,
                $"feetY={feetY:0.#} cx={cx:0.#}");
        }

        sb.AppendLine();
        sb.AppendLine($"SUMMARY: {pass} passed, {fail} failed");
        File.WriteAllText(outFile, sb.ToString());
    }
}
