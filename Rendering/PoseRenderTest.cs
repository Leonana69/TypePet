using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TypePet.Rendering;

/// <summary>
/// Dev-only harness: renders the character poses to PNGs on a clean background (with a ground line
/// at the foot anchor and a vertical line through the navel) so the sprite assembly, anchoring, and
/// left/right flip can be eyeballed offscreen. Triggered by <c>--render-poses &lt;dir&gt;</c>.
///
/// <c>--render-from</c> takes either an embedded footage path (e.g. <c>Assets/footage</c>) or an
/// on-disk character folder (one containing a <c>manifest.json</c>); a disk folder also exercises the
/// face expressions, rendering each over the JUMP pose (the drag pose) so the brow anchoring can be
/// eyeballed too.
/// </summary>
public static class PoseRenderTest
{
    public static void Run(string outDir, string? footageDir = null)
    {
        Directory.CreateDirectory(outDir);

        // A footageDir that points at a real folder with a manifest is loaded from disk (and pulls in
        // expressions); otherwise it's treated as an embedded avares path like "Assets/footage".
        bool fromDisk = footageDir is not null && File.Exists(Path.Combine(footageDir, "manifest.json"));
        var sprites = fromDisk
            ? CharacterSprites.LoadFromDirectory(footageDir!, hitTestPoses: CharacterAnimator.ActivePoses, loadExpressions: true)
            : footageDir is null
                ? CharacterSprites.Load(hitTestPoses: CharacterAnimator.ActivePoses)
                : CharacterSprites.Load(footageDir: footageDir, hitTestPoses: CharacterAnimator.ActivePoses);
        if (sprites is null)
        {
            File.WriteAllText(Path.Combine(outDir, "FAILED.txt"), "CharacterSprites load returned null");
            return;
        }
        File.WriteAllText(Path.Combine(outDir, "info.txt"),
            $"HalfWidth={sprites.HalfWidth} HeightAboveFeet={sprites.HeightAboveFeet} " +
            $"Expressions={string.Join(",", sprites.ExpressionNames)}");

        const int W = 140, H = 190;
        var bg = new SolidColorBrush(Color.FromRgb(232, 232, 238));
        var groundPen = new Pen(new SolidColorBrush(Color.FromRgb(110, 110, 120)), 1);
        var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 200, 70, 70)), 1);

        void RenderFrame(string file, string pose, int frame, bool flip,
            CharacterSprites.Expression? expr, int exprFrame)
        {
            var rtb = new RenderTargetBitmap(new PixelSize(W, H), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawRectangle(bg, null, new Avalonia.Rect(0, 0, W, H));
                double groundY = H - 20;
                double centerX = W / 2.0;
                ctx.DrawLine(groundPen, new Avalonia.Point(0, groundY), new Avalonia.Point(W, groundY));
                ctx.DrawLine(centerPen, new Avalonia.Point(centerX, 0), new Avalonia.Point(centerX, H));
                PetRenderer.DrawCharacter(ctx, sprites, pose, frame, centerX, groundY, flip, expr, exprFrame);
            }
            rtb.Save(Path.Combine(outDir, file));
            rtb.Dispose();
        }

        foreach (var pose in new[] { "stand1", "walk1", "jump", "ladder", "rope" })
        {
            var p = sprites.GetPose(pose);
            if (p is null) continue;
            for (int f = 0; f < p.Frames.Count; f++)
                foreach (bool flip in new[] { false, true })
                    RenderFrame($"{pose}_f{f}_{(flip ? "R" : "L")}.png", pose, f, flip, null, 0);
        }

        // One PNG per expression over the JUMP pose (what the pet shows while dragged), at the
        // expression's first frame, so the brow placement can be checked against the neutral face.
        foreach (var name in sprites.ExpressionNames)
        {
            var expr = sprites.GetExpression(name);
            if (expr is null) continue;
            RenderFrame($"expr_{name}.png", "jump", 0, flip: false, expr, 0);
        }
    }
}
