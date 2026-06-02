using System.IO;
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

        sb.AppendLine();
        sb.AppendLine($"SUMMARY: {pass} passed, {fail} failed");
        File.WriteAllText(outFile, sb.ToString());
    }
}
