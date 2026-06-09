// TypePet default-character generator — ORIGINAL ART, no third-party game assets.
// Draws a small round "blob" mascot procedurally and emits one composited PNG per
// animation frame plus a minimal manifest.json that the TypePet renderer consumes
// (it reads only: animations -> { navel, playbackCycle, frames[] -> { delayMs, draw[] -> {category,image,canvasX,canvasY,width,height} } }).
//
// Dependency-free: a tiny supersampled software rasterizer + a hand-rolled PNG writer (BCL only).
// Usage: dotnet run -- <outputDir>     (default outputDir = ./DefaultCharacterNew)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

class Program
{
    // ---- canvas / anchor constants (logical pixels) ----
    const int SS = 4;            // supersample factor for anti-aliasing
    const int CW = 120;          // logical canvas width
    const int CH = 116;          // logical canvas height
    const double Cx = 60;        // horizontal center (also navel X — keeps mirror symmetric)
    const double GroundY = 100;  // feet baseline

    static int W => CW * SS;
    static int H => CH * SS;

    // straight-alpha RGBA buffers at supersampled resolution
    static double[] Rb, Gb, Bb, Ab;

    // ---- palette (original mint blob) ----
    static readonly (double r, double g, double b) OUTLINE = (39, 52, 59);
    static readonly (double r, double g, double b) BODY = (95, 201, 184);
    static readonly (double r, double g, double b) BODY_D = (72, 174, 158);
    static readonly (double r, double g, double b) BELLY = (197, 242, 234);
    static readonly (double r, double g, double b) FOOT = (72, 162, 150);
    static readonly (double r, double g, double b) EYE = (39, 52, 59);
    static readonly (double r, double g, double b) WHITE = (255, 255, 255);
    static readonly (double r, double g, double b) CHEEK = (255, 150, 162);
    static readonly (double r, double g, double b) GOLD = (255, 213, 120);

    static string OutDir;
    static readonly StringBuilder Manifest = new();
    static readonly List<string> AnimJson = new();

    // all-pose preview capture (first frame of each pose, full 120x116 buffer)
    static bool CapturePreview = false;
    static readonly List<(string pose, byte[] full)> PreviewShots = new();

    static int Main(string[] args)
    {
        // --arm-preview <outDir>: render a side-by-side sheet of the candidate arm styles
        // on the idle (stand1) pose so the user can pick one. Does not touch the real assets.
        if (args.Length > 0 && args[0] == "--arm-preview")
        {
            string dir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "ArmPreview");
            ArmPreview(dir);
            Console.WriteLine($"Arm preview -> {dir}");
            return 0;
        }

        // --all-preview <outDir>: render every pose's first frame into one labelled grid.
        if (args.Length > 0 && args[0] == "--all-preview")
        {
            string dir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "AllPreview");
            AllPreview(dir);
            Console.WriteLine($"All-pose preview -> {dir}");
            return 0;
        }

        // --arm <style> <outDir>: generate the full character set using the chosen arm style.
        if (args.Length > 0 && args[0] == "--arm")
        {
            ArmMode = Enum.Parse<ArmStyle>(args[1], ignoreCase: true);
            OutDir = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "DefaultCharacterNew");
            args = new[] { OutDir }; // fall through to normal generation
        }

        OutDir = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "DefaultCharacterNew");
        Directory.CreateDirectory(Path.Combine(OutDir, "Body"));

        BuildStand1();
        BuildStand2();
        BuildWalk();      // emits walk1 art; aliased to walk2
        BuildLadder();    // emits ladder art; aliased to rope
        BuildJump();
        BuildProne();     // emits prone + proneStab
        BuildSit();
        BuildAlert();
        BuildHeal();
        BuildFly();
        BuildSwing();     // swingO1
        BuildStab();      // stabO1

        WriteManifest();
        Console.WriteLine($"Done -> {OutDir}");
        return 0;
    }

    // =====================================================================
    //  Pose options + the creature draw routine
    // =====================================================================

    // Arm rendering style. Droop = the original (long capsules hanging in front).
    // The rest are the shorter / not-in-front candidates the user is choosing between.
    enum ArmStyle { Droop, Stub, Paw, Fin, Tuck }

    // The arm style used by the real generation. User picked "side fins" (drawn behind
    // the body so only the outer curve peeks past the silhouette). Override with `--arm`.
    static ArmStyle ArmMode = ArmStyle.Fin;

    class Opt
    {
        public double bodyW = 46, bodyH = 54;     // capsule size
        public double bodyCx = Cx, bodyCy = 66;   // body center
        public double sx = 1, sy = 1;             // squash/stretch
        public double lean = 0;                   // horizontal shift of body+face
        // feet (relative to Cx; absolute y). null y => hidden
        public double footLx = -13, footLy = 95, footRx = 13, footRy = 95;
        public bool feet = true;
        // hands (relative to Cx; absolute y) — Fin resting tuck (peeks at the sides)
        public double handLx = -28, handLy = 74, handRx = 28, handRy = 74;
        public bool arms = true;
        // arm style; defaults to the globally-selected mode so every pose follows the pick
        public ArmStyle arm = ArmMode;
        // face
        public string eyes = "open";   // open | blink | happy | wide
        public string mouth = "smile"; // smile | open | small | none
        public bool cheeks = true;
        public double eyeDX = 11, eyeUp = 7;
        // extras
        public bool wings = false; public double wingUp = 0;
        public bool plus = false;
        public bool prone = false;     // lying-down layout
    }

    static void DrawCreature(Opt o)
    {
        double bw = o.bodyW * o.sx, bh = o.bodyH * o.sy;
        double bx = o.bodyCx + o.lean, by = o.bodyCy;

        if (o.prone)
        {
            // lying flat: a wide low capsule, eyes near the left (facing) side, little nubs.
            double w = o.bodyW, h = o.bodyH;
            OutlinedRound(bx, by, w, h, h / 2, BODY);
            FillEllipse(bx - w * 0.10, by + 2, w * 0.30, h * 0.30, BELLY, 1);
            // eyes on the left third
            double ex = bx - w * 0.28, ey = by - 2;
            DrawEyes(ex, ey, o.eyes, 1);
            if (o.mouth != "none") DrawMouth(ex + 7, ey + 7, o.mouth);
            if (o.cheeks) FillEllipse(ex - 7, ey + 6, 4, 3, CHEEK, 0.7);
            return;
        }

        // wings behind body (fly)
        if (o.wings)
        {
            double wy = by - 2 - o.wingUp;
            OutlinedEllipse(bx - bw * 0.5 - 8, wy, 11, 7, BELLY);
            OutlinedEllipse(bx + bw * 0.5 + 8, wy, 11, 7, BELLY);
        }

        // feet (behind body so the body overlaps their tops)
        if (o.feet)
        {
            OutlinedEllipse(o.bodyCx + o.footLx, o.footLy, 9, 5.5, FOOT);
            OutlinedEllipse(o.bodyCx + o.footRx, o.footRy, 9, 5.5, FOOT);
        }

        // arms drawn BEHIND the body (Fin) — only the outer part peeks past the silhouette
        if (o.arms && o.arm == ArmStyle.Fin) DrawArms(o, bx, by, bw, bh);

        // body
        OutlinedRound(bx, by, bw, bh, Math.Min(bw, bh) / 2, BODY);
        // belly highlight
        FillEllipse(bx, by + bh * 0.12, bw * 0.30, bh * 0.34, BELLY, 1);

        // face
        double fy = by - o.eyeUp;
        DrawEyes(bx - o.eyeDX, fy, o.eyes, 1);
        DrawEyes(bx + o.eyeDX, fy, o.eyes, 1, mirror: true);
        if (o.cheeks)
        {
            FillEllipse(bx - 17, fy + 8, 4.5, 3, CHEEK, 0.7);
            FillEllipse(bx + 17, fy + 8, 4.5, 3, CHEEK, 0.7);
        }
        if (o.mouth != "none") DrawMouth(bx, fy + 9, o.mouth);

        // arms drawn IN FRONT of the body (all styles except Fin)
        if (o.arms && o.arm != ArmStyle.Fin) DrawArms(o, bx, by, bw, bh);

        // floating plus marks (heal)
        if (o.plus)
        {
            Plus(bx - 16, by - 34); Plus(bx + 12, by - 30); Plus(bx - 2, by - 42);
        }
    }

    // Draw the two arms for a non-prone creature. The hand targets (o.handL*/handR*)
    // are the arm END points; the style controls where the arm attaches to the body,
    // its thickness, whether it ends in a distinct round hand, and its colour.
    static void DrawArms(Opt o, double bx, double by, double bw, double bh)
    {
        double hlx = o.bodyCx + o.handLx, hly = o.handLy;
        double hrx = o.bodyCx + o.handRx, hry = o.handRy;
        switch (o.arm)
        {
            case ArmStyle.Droop: // original: long capsules from high on the body, in front
            {
                double sh = by + 2;
                OutlinedCapsule(bx - bw * 0.42, sh, hlx, hly, 4.5, BODY_D);
                OutlinedCapsule(bx + bw * 0.42, sh, hrx, hry, 4.5, BODY_D);
                break;
            }
            case ArmStyle.Stub: // short tapered nubs attached low at the sides
            {
                double sh = by + bh * 0.16;
                OutlinedCapsule(bx - bw * 0.40, sh, hlx, hly, 3.8, BODY_D);
                OutlinedCapsule(bx + bw * 0.40, sh, hrx, hry, 3.8, BODY_D);
                break;
            }
            case ArmStyle.Paw: // short thin connector ending in a distinct round paw
            {
                double sh = by + bh * 0.12;
                OutlinedCapsule(bx - bw * 0.40, sh, hlx, hly, 2.8, BODY_D);
                OutlinedCapsule(bx + bw * 0.40, sh, hrx, hry, 2.8, BODY_D);
                OutlinedEllipse(hlx, hly, 4.6, 4.6, BODY_D);
                OutlinedEllipse(hrx, hry, 4.6, 4.6, BODY_D);
                break;
            }
            case ArmStyle.Fin: // flipper drawn behind the body; only the outer curve shows
            {
                double sh = by + bh * 0.02;
                OutlinedCapsule(bx - bw * 0.28, sh, hlx, hly, 5.0, BODY_D);
                OutlinedCapsule(bx + bw * 0.28, sh, hrx, hry, 5.0, BODY_D);
                break;
            }
            case ArmStyle.Tuck: // tiny body-colour bumps that merge into the silhouette
            {
                double sh = by + bh * 0.20;
                OutlinedCapsule(bx - bw * 0.42, sh, hlx, hly, 5.2, BODY);
                OutlinedCapsule(bx + bw * 0.42, sh, hrx, hry, 5.2, BODY);
                break;
            }
        }
    }

    static void DrawEyes(double ex, double ey, string kind, double a, bool mirror = false)
    {
        switch (kind)
        {
            case "blink":
                Capsule(ex - 4, ey, ex + 4, ey, 1.5, EYE, a);
                break;
            case "happy": // upward caret ^
                Capsule(ex - 4, ey + 2, ex, ey - 3, 1.5, EYE, a);
                Capsule(ex, ey - 3, ex + 4, ey + 2, 1.5, EYE, a);
                break;
            case "wide":
                FillEllipse(ex, ey, 6, 7, EYE, a);
                FillEllipse(ex - 1.6, ey - 2.4, 2.0, 2.2, WHITE, a);
                break;
            default: // open
                FillEllipse(ex, ey, 5, 6, EYE, a);
                FillEllipse(ex - 1.5, ey - 2, 1.7, 1.9, WHITE, a);
                break;
        }
    }

    static void DrawMouth(double mx, double my, string kind)
    {
        switch (kind)
        {
            case "open":
                FillEllipse(mx, my + 1, 3.5, 4.5, EYE, 1);
                break;
            case "small":
                FillEllipse(mx, my, 1.8, 1.8, EYE, 1);
                break;
            default: // smile (∪)
                Capsule(mx - 4, my - 1, mx, my + 2, 1.4, EYE, 1);
                Capsule(mx, my + 2, mx + 4, my - 1, 1.4, EYE, 1);
                break;
        }
    }

    static void Plus(double x, double y)
    {
        Capsule(x - 3, y, x + 3, y, 1.3, GOLD, 1);
        Capsule(x, y - 3, x, y + 3, 1.3, GOLD, 1);
    }

    // =====================================================================
    //  Poses
    // =====================================================================
    static void BuildStand1()
    {
        var frames = new List<(int, Action)>();
        frames.Add((600, () => { var o = new Opt { sy = 1.00 }; DrawCreature(o); }));
        frames.Add((600, () => { var o = new Opt { sy = 1.04, bodyCy = 65 }; DrawCreature(o); }));
        frames.Add((600, () => { var o = new Opt { sy = 1.00, eyes = "blink" }; DrawCreature(o); }));
        EmitPose("stand1", 68, frames, new[] { 0, 1, 0, 2 });
    }

    static void BuildStand2()
    {
        var frames = new List<(int, Action)>();
        frames.Add((550, () => { var o = new Opt { handLx = -29, handRx = 29, handLy = 78, handRy = 78 }; DrawCreature(o); }));
        frames.Add((550, () => { var o = new Opt { sy = 1.03, bodyCy = 65, handLx = -31, handRx = 31 }; DrawCreature(o); }));
        frames.Add((550, () => { var o = new Opt { eyes = "happy", mouth = "open" }; DrawCreature(o); }));
        EmitPose("stand2", 68, frames, new[] { 0, 1, 2, 1 });
    }

    static void BuildWalk()
    {
        var frames = new List<(int, Action)>();
        // Fin paddle: side fins bob up/down opposite the feet. (x kept out so they peek.)
        // stride A: left foot fwd (toward -x = facing left), left fin up / right fin down
        frames.Add((170, () => DrawCreature(new Opt
        {
            lean = -2, footLx = -18, footLy = 96, footRx = 9, footRy = 93,
            handLx = -29, handLy = 70, handRx = 29, handRy = 82,
        })));
        // passing + bob up: fins level
        frames.Add((170, () => DrawCreature(new Opt
        {
            sy = 1.03, bodyCy = 65, footLx = -12, footLy = 94, footRx = 12, footRy = 94,
            handLx = -28, handLy = 76, handRx = 28, handRy = 76,
        })));
        // stride B: right foot fwd, right fin up / left fin down
        frames.Add((170, () => DrawCreature(new Opt
        {
            lean = -2, footLx = -9, footLy = 93, footRx = 18, footRy = 96,
            handLx = -29, handLy = 82, handRx = 29, handRy = 70,
        })));
        // passing + bob up
        frames.Add((170, () => DrawCreature(new Opt
        {
            sy = 1.03, bodyCy = 65, footLx = -12, footLy = 94, footRx = 12, footRy = 94,
            handLx = -28, handLy = 76, handRx = 28, handRy = 76,
        })));
        EmitPose("walk1", 68, frames, new[] { 0, 1, 2, 3 });
    }

    static void BuildLadder()
    {
        var frames = new List<(int, Action)>();
        // climbing: narrow body, side fins reach high/low alternately (kept OUT past the
        // silhouette so the behind-body fin actually shows), feet alternating to match.
        frames.Add((260, () => DrawCreature(new Opt
        {
            sx = 0.90, eyes = "open", mouth = "small", cheeks = false,
            handLx = -29, handLy = 50, handRx = 28, handRy = 76,
            footLx = -11, footLy = 92, footRx = 12, footRy = 97,
        })));
        frames.Add((260, () => DrawCreature(new Opt
        {
            sx = 0.90, eyes = "open", mouth = "small", cheeks = false,
            handLx = -28, handLy = 76, handRx = 29, handRy = 50,
            footLx = -12, footLy = 97, footRx = 11, footRy = 92,
        })));
        EmitPose("ladder", 68, frames, new[] { 0, 1 });
    }

    static void BuildJump()
    {
        // Animated fin swing: both side-fins sweep up→mid→down (like arms swinging on a jump)
        // with a small volume-preserving squash/stretch synced to the swing. Feet stay put so the
        // body just squashes from the foot line. Frame 0 (fins up) is the launch — it always shows
        // first because a new jump restarts the pose from frame 0, so even a brief hop reads right.
        var frames = new List<(int, Action)>();
        // up — launch: stretched tall, fins flung high
        frames.Add((120, () => DrawCreature(new Opt
        {
            sx = 0.92, sy = 1.12, bodyCy = 62, eyes = "wide", mouth = "open",
            handLx = -30, handLy = 49, handRx = 30, handRy = 49,
            footLx = -10, footLy = 86, footRx = 10, footRy = 86,
        })));
        // mid — fins sweeping down past the sides
        frames.Add((110, () => DrawCreature(new Opt
        {
            sx = 0.96, sy = 1.05, bodyCy = 62, eyes = "wide", mouth = "open",
            handLx = -31, handLy = 63, handRx = 31, handRy = 63,
            footLx = -10, footLy = 86, footRx = 10, footRy = 86,
        })));
        // down — fins low at the sides, body squashed back to neutral
        frames.Add((120, () => DrawCreature(new Opt
        {
            sx = 1.00, sy = 1.00, bodyCy = 63, eyes = "wide", mouth = "open",
            handLx = -29, handLy = 77, handRx = 29, handRy = 77,
            footLx = -10, footLy = 86, footRx = 10, footRy = 86,
        })));
        EmitPose("jump", 64, frames, new[] { 0, 1, 2, 1 });
    }

    static void BuildProne()
    {
        // prone (rest)
        var rest = new List<(int, Action)>();
        rest.Add((150, () => DrawCreature(new Opt { prone = true, bodyW = 78, bodyH = 34, bodyCy = 84, eyes = "blink", mouth = "small" })));
        EmitPose("prone", 84, rest, new[] { 0 });

        // proneStab (rest -> poke to the left)
        var stab = new List<(int, Action)>();
        stab.Add((300, () => DrawCreature(new Opt { prone = true, bodyW = 78, bodyH = 34, bodyCy = 84, eyes = "open", mouth = "small" })));
        stab.Add((350, () =>
        {
            DrawCreature(new Opt { prone = true, bodyW = 78, bodyH = 34, bodyCy = 84, eyes = "open", mouth = "open" });
            // thrust nub to the left
            OutlinedCapsule(Cx - 30, 84, Cx - 52, 86, 4.5, BODY_D);
        }));
        EmitPose("proneStab", 84, stab, new[] { 0, 1 });
    }

    static void BuildSit()
    {
        var frames = new List<(int, Action)>();
        frames.Add((150, () => DrawCreature(new Opt
        {
            sx = 1.08, sy = 0.86, bodyCy = 72, eyes = "happy", mouth = "smile",
            footLx = -16, footLy = 96, footRx = 16, footRy = 96,
            handLx = -24, handLy = 86, handRx = 24, handRy = 86,
        })));
        EmitPose("sit", 74, frames, new[] { 0 });
    }

    static void BuildAlert()
    {
        var frames = new List<(int, Action)>();
        frames.Add((130, () => DrawCreature(new Opt
        {
            sy = 1.06, bodyCy = 64, eyes = "wide", mouth = "open",
            handLx = -29, handLy = 60, handRx = 29, handRy = 60,
        })));
        frames.Add((130, () => DrawCreature(new Opt
        {
            sy = 1.06, bodyCy = 64, lean = 1.5, eyes = "wide", mouth = "open",
            handLx = -29, handLy = 58, handRx = 29, handRy = 58,
        })));
        frames.Add((130, () => DrawCreature(new Opt
        {
            sy = 1.06, bodyCy = 64, lean = -1.5, eyes = "wide", mouth = "open",
            handLx = -29, handLy = 60, handRx = 29, handRy = 60,
        })));
        EmitPose("alert", 66, frames, new[] { 0, 1, 2, 1 });
    }

    static void BuildHeal()
    {
        var frames = new List<(int, Action)>();
        frames.Add((300, () => DrawCreature(new Opt { eyes = "happy", mouth = "smile" })));
        frames.Add((150, () => DrawCreature(new Opt { sy = 1.05, bodyCy = 64, eyes = "happy", mouth = "open", plus = true, handLx = -30, handRx = 30, handLy = 74, handRy = 74 })));
        frames.Add((350, () => DrawCreature(new Opt { eyes = "happy", mouth = "smile", plus = true })));
        EmitPose("heal", 68, frames, new[] { 0, 1, 2 });
    }

    static void BuildFly()
    {
        var frames = new List<(int, Action)>();
        frames.Add((260, () => DrawCreature(new Opt
        {
            bodyCy = 60, feet = false, wings = true, wingUp = 0, eyes = "open", mouth = "smile",
            handLx = -28, handLy = 70, handRx = 28, handRy = 70,
        })));
        frames.Add((260, () => DrawCreature(new Opt
        {
            bodyCy = 58, feet = false, wings = true, wingUp = 6, eyes = "open", mouth = "smile",
            handLx = -28, handLy = 68, handRx = 28, handRy = 68,
        })));
        EmitPose("fly", 62, frames, new[] { 0, 1 });
    }

    static void BuildSwing()
    {
        var frames = new List<(int, Action)>();
        // windup (lean back, arm up-right)
        frames.Add((120, () => DrawCreature(new Opt
        {
            lean = 3, eyes = "wide", mouth = "small",
            handLx = -24, handLy = 84, handRx = 26, handRy = 50,
        })));
        // mid
        frames.Add((90, () => DrawCreature(new Opt
        {
            lean = 0, eyes = "wide", mouth = "open",
            handLx = -24, handLy = 84, handRx = 8, handRy = 46,
        })));
        // strike (lean forward, arm swept down-left)
        frames.Add((150, () => DrawCreature(new Opt
        {
            lean = -4, eyes = "wide", mouth = "open",
            handLx = -22, handLy = 84, handRx = -34, handRy = 78,
        })));
        EmitPose("swingO1", 68, frames, new[] { 0, 1, 2 });
    }

    static void BuildStab()
    {
        var frames = new List<(int, Action)>();
        // coil
        frames.Add((140, () => DrawCreature(new Opt
        {
            lean = 4, eyes = "wide", mouth = "small",
            handLx = -22, handLy = 82, handRx = 22, handRy = 72,
        })));
        // thrust to the left
        frames.Add((180, () => DrawCreature(new Opt
        {
            lean = -3, sx = 1.04, eyes = "wide", mouth = "open",
            handLx = -40, handLy = 74, handRx = 18, handRy = 78,
        })));
        EmitPose("stabO1", 68, frames, new[] { 0, 1 });
    }

    // =====================================================================
    //  Emit: render each frame, crop, write PNG, accumulate manifest JSON
    // =====================================================================
    static void EmitPose(string pose, double navelY, List<(int delay, Action draw)> frames, int[] cycle)
    {
        var frameJson = new List<string>();
        for (int i = 0; i < frames.Count; i++)
        {
            Clear();
            frames[i].draw();
            if (CapturePreview) PreviewShots.Add(($"{pose}_f{i}", DownsampleFull()));
            var (rgba, w, h, ox, oy) = Downsample();
            string img = $"Body/{pose}_f{i:00}.png";
            WritePng(Path.Combine(OutDir, img.Replace('/', Path.DirectorySeparatorChar)), w, h, rgba);

            string draw = "{\"category\":\"Body\",\"layer\":\"body\",\"image\":\"" + img + "\"," +
                          $"\"canvasX\":{ox},\"canvasY\":{oy},\"width\":{w},\"height\":{h}}}";
            frameJson.Add($"{{\"delayMs\":{frames[i].delay},\"draw\":[{draw}]}}");
        }
        string cyc = string.Join(",", cycle);
        string anim = $"\"{pose}\":{{\"navel\":{{\"x\":{(int)Cx},\"y\":{(int)navelY}}}," +
                      $"\"playbackCycle\":[{cyc}],\"frames\":[{string.Join(",", frameJson)}]}}";
        AnimJson.Add(anim);
        // aliases that reuse identical art
        if (pose == "walk1") AnimJson.Add(anim.Replace("\"walk1\"", "\"walk2\"").Replace("walk1_f", "walk1_f")); // walk2 reuses walk1 images
        if (pose == "ladder") AnimJson.Add(anim.Replace("\"ladder\"", "\"rope\""));
        Console.WriteLine($"  {pose}: {frames.Count} frame(s)");
    }

    static void WriteManifest()
    {
        var sb = new StringBuilder();
        sb.Append("{\"tool\":\"typepet-chargen\",\"schema\":2,\"facing\":\"left\",\"flip\":false,\"scale\":1,");
        sb.Append("\"items\":[{\"category\":\"Body\",\"folder\":\"Body\"}],");
        sb.Append("\"animations\":{");
        sb.Append(string.Join(",", AnimJson));
        sb.Append("}}");
        File.WriteAllText(Path.Combine(OutDir, "manifest.json"), sb.ToString());
    }

    // =====================================================================
    //  Arm-style preview (compare candidates on the idle pose)
    // =====================================================================
    static void ArmPreview(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // label, style, idle (stand1) resting hand X offset + Y for that style
        var opts = new (string label, ArmStyle style, double hx, double hy)[]
        {
            ("0", ArmStyle.Droop, 27, 80),  // current — for reference
            ("1", ArmStyle.Stub,  25, 74),
            ("2", ArmStyle.Paw,   26, 77),
            ("3", ArmStyle.Fin,   28, 74),
            ("4", ArmStyle.Tuck,  24, 76),
        };

        var fulls = new List<byte[]>();
        foreach (var op in opts)
        {
            Clear();
            DrawCreature(new Opt { arm = op.style, handLx = -op.hx, handRx = op.hx, handLy = op.hy, handRy = op.hy });
            var full = DownsampleFull();
            fulls.Add(full);
            var c = CropAndScale(full, 5);
            WritePng(Path.Combine(outDir, $"opt{op.label}.png"), c.w, c.h, c.rgba);
        }

        BuildSheet(Path.Combine(outDir, "arm-options.png"), opts, fulls);
    }

    static void BuildSheet(string path, (string label, ArmStyle style, double hx, double hy)[] opts, List<byte[]> fulls)
    {
        const int Z = 5;                                  // sprite zoom in the sheet
        const int x0 = 16, y0 = 30, winW = 88, winH = 78; // source window in the 120x116 canvas
        const int S = 6;                                  // digit pixel size
        int cellW = winW * Z, cellH = winH * Z, gap = 18, labelH = 5 * S + 12;
        int n = opts.Length;
        int sheetW = gap + n * (cellW + gap);
        int sheetH = labelH + cellH + gap;
        var sh = new byte[sheetW * sheetH * 4];
        for (int i = 0; i < sheetW * sheetH; i++) { sh[i * 4] = 236; sh[i * 4 + 1] = 239; sh[i * 4 + 2] = 242; sh[i * 4 + 3] = 255; }

        for (int k = 0; k < n; k++)
        {
            int cellX = gap + k * (cellW + gap), cellY = labelH;
            var full = fulls[k];
            for (int sy = 0; sy < winH; sy++)
                for (int sx = 0; sx < winW; sx++)
                {
                    int si = ((y0 + sy) * CW + (x0 + sx)) * 4;
                    double a = full[si + 3] / 255.0;
                    if (a <= 0) continue;
                    var c = ((int)full[si], (int)full[si + 1], (int)full[si + 2]);
                    for (int dy = 0; dy < Z; dy++)
                        for (int dx = 0; dx < Z; dx++)
                            SheetBlend(sh, sheetW, cellX + sx * Z + dx, cellY + sy * Z + dy, c, a);
                }
            DrawDigit(sh, sheetW, opts[k].label[0], cellX + cellW / 2 - (3 * S) / 2, 6, S, (40, 50, 60));
        }
        WritePng(path, sheetW, sheetH, sh);
    }

    static void SheetBlend(byte[] sh, int sw, int x, int y, (int r, int g, int b) c, double a)
    {
        if (x < 0 || y < 0 || x >= sw) return;
        int o = (y * sw + x) * 4;
        if (o < 0 || o + 3 >= sh.Length) return;
        sh[o + 0] = (byte)Math.Clamp(c.r * a + sh[o + 0] * (1 - a), 0, 255);
        sh[o + 1] = (byte)Math.Clamp(c.g * a + sh[o + 1] * (1 - a), 0, 255);
        sh[o + 2] = (byte)Math.Clamp(c.b * a + sh[o + 2] * (1 - a), 0, 255);
        sh[o + 3] = 255;
    }

    static string[] Glyph(char c) => c switch
    {
        '0' => new[] { "###", "#.#", "#.#", "#.#", "###" },
        '1' => new[] { ".#.", "##.", ".#.", ".#.", "###" },
        '2' => new[] { "###", "..#", "###", "#..", "###" },
        '3' => new[] { "###", "..#", "###", "..#", "###" },
        '4' => new[] { "#.#", "#.#", "###", "..#", "..#" },
        '5' => new[] { "###", "#..", "###", "..#", "###" },
        '6' => new[] { "###", "#..", "###", "#.#", "###" },
        '7' => new[] { "###", "..#", "..#", "..#", "..#" },
        '8' => new[] { "###", "#.#", "###", "#.#", "###" },
        '9' => new[] { "###", "#.#", "###", "..#", "###" },
        _   => new[] { "...", "...", "...", "...", "..." },
    };

    static void DrawDigit(byte[] sh, int sw, char ch, int x, int y, int S, (int, int, int) col)
    {
        var g = Glyph(ch);
        for (int ry = 0; ry < 5; ry++)
            for (int rx = 0; rx < 3; rx++)
                if (g[ry][rx] == '#')
                    for (int dy = 0; dy < S; dy++)
                        for (int dx = 0; dx < S; dx++)
                            SheetBlend(sh, sw, x + rx * S + dx, y + ry * S + dy, col, 1);
    }

    static void DrawNumber(byte[] sh, int sw, int n, int x, int y, int S, (int, int, int) col)
    {
        string s = n.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < s.Length; i++)
            DrawDigit(sh, sw, s[i], x + i * (4 * S), y, S, col);
    }

    // Render the first frame of every pose into one labelled grid so all poses can be
    // eyeballed at once (the app's --render-poses skips the attack stances).
    static void AllPreview(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "Body"));
        OutDir = outDir;
        CapturePreview = true;
        BuildStand1(); BuildStand2(); BuildWalk(); BuildLadder(); BuildJump();
        BuildProne(); BuildSit(); BuildAlert(); BuildHeal(); BuildFly(); BuildSwing(); BuildStab();

        int n = PreviewShots.Count;
        const int Z = 3, x0 = 8, y0 = 14, winW = 104, winH = 96, S = 4;
        int cols = 5, rows = (n + cols - 1) / cols;
        int cellW = winW * Z, cellH = winH * Z, gap = 12, labelH = 5 * S + 8;
        int cw = cellW + gap, ch = labelH + cellH + gap;
        int sheetW = gap + cols * cw, sheetH = gap + rows * ch;
        var sh = new byte[sheetW * sheetH * 4];
        for (int i = 0; i < sheetW * sheetH; i++) { sh[i * 4] = 236; sh[i * 4 + 1] = 239; sh[i * 4 + 2] = 242; sh[i * 4 + 3] = 255; }

        for (int k = 0; k < n; k++)
        {
            int cxi = k % cols, cyi = k / cols;
            int cellX = gap + cxi * cw, baseY = gap + cyi * ch;
            DrawNumber(sh, sheetW, k, cellX + 2, baseY, S, (40, 50, 60));
            int cellY = baseY + labelH;
            var full = PreviewShots[k].full;
            for (int sy = 0; sy < winH; sy++)
                for (int sx = 0; sx < winW; sx++)
                {
                    int si = ((y0 + sy) * CW + (x0 + sx)) * 4;
                    double a = full[si + 3] / 255.0;
                    if (a <= 0) continue;
                    var c = ((int)full[si], (int)full[si + 1], (int)full[si + 2]);
                    for (int dy = 0; dy < Z; dy++)
                        for (int dx = 0; dx < Z; dx++)
                            SheetBlend(sh, sheetW, cellX + sx * Z + dx, cellY + sy * Z + dy, c, a);
                }
        }
        WritePng(Path.Combine(outDir, "all-poses.png"), sheetW, sheetH, sh);
        for (int k = 0; k < n; k++) Console.WriteLine($"  [{k}] {PreviewShots[k].pose}");
    }

    // Crop a full 120x116 render to its tight alpha bbox and nearest-neighbour scale by Z.
    static (byte[] rgba, int w, int h) CropAndScale(byte[] full, int Z)
    {
        int minx = CW, miny = CH, maxx = -1, maxy = -1;
        for (int y = 0; y < CH; y++)
            for (int x = 0; x < CW; x++)
                if (full[(y * CW + x) * 4 + 3] > 2)
                {
                    if (x < minx) minx = x; if (x > maxx) maxx = x;
                    if (y < miny) miny = y; if (y > maxy) maxy = y;
                }
        if (maxx < 0) return (new byte[4], 1, 1);
        int w = maxx - minx + 1, h = maxy - miny + 1;
        var outp = new byte[w * Z * h * Z * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = ((y + miny) * CW + (x + minx)) * 4;
                for (int dy = 0; dy < Z; dy++)
                    for (int dx = 0; dx < Z; dx++)
                    {
                        int di = ((y * Z + dy) * (w * Z) + (x * Z + dx)) * 4;
                        outp[di] = full[si]; outp[di + 1] = full[si + 1]; outp[di + 2] = full[si + 2]; outp[di + 3] = full[si + 3];
                    }
            }
        return (outp, w * Z, h * Z);
    }

    // =====================================================================
    //  Rasterizer
    // =====================================================================
    static void Clear()
    {
        int n = W * H;
        Rb = new double[n]; Gb = new double[n]; Bb = new double[n]; Ab = new double[n];
    }

    static void Blend(int x, int y, (double r, double g, double b) c, double a)
    {
        if (x < 0 || y < 0 || x >= W || y >= H || a <= 0) return;
        int i = y * W + x;
        double da = Ab[i];
        double na = a + da * (1 - a);
        if (na <= 1e-9) { Ab[i] = 0; return; }
        Rb[i] = (c.r * a + Rb[i] * da * (1 - a)) / na;
        Gb[i] = (c.g * a + Gb[i] * da * (1 - a)) / na;
        Bb[i] = (c.b * a + Bb[i] * da * (1 - a)) / na;
        Ab[i] = na;
    }

    static void FillEllipse(double cx, double cy, double rx, double ry, (double, double, double) col, double a)
    {
        int x0 = (int)Math.Floor((cx - rx) * SS), x1 = (int)Math.Ceiling((cx + rx) * SS);
        int y0 = (int)Math.Floor((cy - ry) * SS), y1 = (int)Math.Ceiling((cy + ry) * SS);
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                double lx = (px + 0.5) / SS, ly = (py + 0.5) / SS;
                double nx = (lx - cx) / rx, ny = (ly - cy) / ry;
                if (nx * nx + ny * ny <= 1) Blend(px, py, col, a);
            }
    }

    static void OutlinedEllipse(double cx, double cy, double rx, double ry, (double, double, double) fill, double ow = 2)
    {
        FillEllipse(cx, cy, rx + ow, ry + ow, OUTLINE, 1);
        FillEllipse(cx, cy, rx, ry, fill, 1);
    }

    static void FillRound(double cx, double cy, double w, double h, double r, (double, double, double) col, double a)
    {
        double hw = w / 2, hh = h / 2; r = Math.Min(r, Math.Min(hw, hh));
        int x0 = (int)Math.Floor((cx - hw) * SS), x1 = (int)Math.Ceiling((cx + hw) * SS);
        int y0 = (int)Math.Floor((cy - hh) * SS), y1 = (int)Math.Ceiling((cy + hh) * SS);
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                double lx = (px + 0.5) / SS, ly = (py + 0.5) / SS;
                double dx = Math.Abs(lx - cx) - (hw - r), dy = Math.Abs(ly - cy) - (hh - r);
                double outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0))
                                 + Math.Min(Math.Max(dx, dy), 0) - r;
                if (outside <= 0) Blend(px, py, col, a);
            }
    }

    static void OutlinedRound(double cx, double cy, double w, double h, double r, (double, double, double) fill, double ow = 2)
    {
        FillRound(cx, cy, w + ow * 2, h + ow * 2, r + ow, OUTLINE, 1);
        FillRound(cx, cy, w, h, r, fill, 1);
    }

    static void Capsule(double x0, double y0, double x1, double y1, double r, (double, double, double) col, double a)
    {
        double minx = Math.Min(x0, x1) - r, maxx = Math.Max(x0, x1) + r;
        double miny = Math.Min(y0, y1) - r, maxy = Math.Max(y0, y1) + r;
        int px0 = (int)Math.Floor(minx * SS), px1 = (int)Math.Ceiling(maxx * SS);
        int py0 = (int)Math.Floor(miny * SS), py1 = (int)Math.Ceiling(maxy * SS);
        double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
        for (int py = py0; py <= py1; py++)
            for (int px = px0; px <= px1; px++)
            {
                double lx = (px + 0.5) / SS, ly = (py + 0.5) / SS;
                double t = len2 <= 1e-9 ? 0 : ((lx - x0) * dx + (ly - y0) * dy) / len2;
                t = Math.Clamp(t, 0, 1);
                double qx = x0 + t * dx, qy = y0 + t * dy;
                double dist = Math.Sqrt((lx - qx) * (lx - qx) + (ly - qy) * (ly - qy));
                if (dist <= r) Blend(px, py, col, a);
            }
    }

    static void OutlinedCapsule(double x0, double y0, double x1, double y1, double r, (double, double, double) fill, double ow = 2)
    {
        Capsule(x0, y0, x1, y1, r + ow, OUTLINE, 1);
        Capsule(x0, y0, x1, y1, r, fill, 1);
    }

    // Average SS x SS blocks (premultiplied) -> 8-bit straight RGBA over the full 120x116 canvas.
    static byte[] DownsampleFull()
    {
        var full = new byte[CW * CH * 4];
        for (int y = 0; y < CH; y++)
            for (int x = 0; x < CW; x++)
            {
                double sr = 0, sg = 0, sb = 0, sa = 0;
                for (int sy = 0; sy < SS; sy++)
                    for (int sx = 0; sx < SS; sx++)
                    {
                        int i = (y * SS + sy) * W + (x * SS + sx);
                        double a = Ab[i];
                        sr += Rb[i] * a; sg += Gb[i] * a; sb += Bb[i] * a; sa += a;
                    }
                int n = SS * SS;
                double oa = sa / n;
                int o = (y * CW + x) * 4;
                if (sa > 1e-6)
                {
                    full[o + 0] = (byte)Math.Clamp(sr / sa, 0, 255);
                    full[o + 1] = (byte)Math.Clamp(sg / sa, 0, 255);
                    full[o + 2] = (byte)Math.Clamp(sb / sa, 0, 255);
                }
                full[o + 3] = (byte)Math.Clamp(oa * 255.0, 0, 255);
            }
        return full;
    }

    // Full render cropped to its tight alpha bbox (what each emitted frame PNG uses).
    static (byte[] rgba, int w, int h, int ox, int oy) Downsample()
    {
        var full = DownsampleFull();

        // tight bbox
        int minx = CW, miny = CH, maxx = -1, maxy = -1;
        for (int y = 0; y < CH; y++)
            for (int x = 0; x < CW; x++)
                if (full[(y * CW + x) * 4 + 3] > 2)
                {
                    if (x < minx) minx = x; if (x > maxx) maxx = x;
                    if (y < miny) miny = y; if (y > maxy) maxy = y;
                }
        if (maxx < 0) { return (new byte[4], 1, 1, (int)Cx, (int)Cx); } // empty guard
        int w = maxx - minx + 1, h = maxy - miny + 1;
        var crop = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Array.Copy(full, ((y + miny) * CW + minx) * 4, crop, y * w * 4, w * 4);
        return (crop, w, h, minx, miny);
    }

    // =====================================================================
    //  PNG writer (RGBA, filter 0, zlib via DeflateStream)
    // =====================================================================
    static void WritePng(string path, int w, int h, byte[] rgba)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);

        int stride = w * 4;
        var raw = new byte[h * (stride + 1)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (stride + 1)] = 0; // filter: none
            Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        uint adler = Adler32(raw);
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        Chunk(fs, "IDAT", ms.ToArray());

        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    static void Chunk(Stream fs, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); fs.Write(len, 0, 4);
        var t = Encoding.ASCII.GetBytes(type);
        fs.Write(t, 0, 4); fs.Write(data, 0, data.Length);
        uint crc = Crc32(t, 0xffffffff); crc = Crc32(data, crc) ^ 0xffffffff;
        var cb = new byte[4]; BE(cb, 0, (int)crc); fs.Write(cb, 0, 4);
    }

    static uint[] _crc;
    static uint Crc32(byte[] data, uint crc)
    {
        if (_crc == null)
        {
            _crc = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
                _crc[n] = c;
            }
        }
        foreach (byte b in data) crc = _crc[(crc ^ b) & 0xff] ^ (crc >> 8);
        return crc;
    }

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
