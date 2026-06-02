using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MaplePet.Rendering;

/// <summary>
/// Dev-only harness: renders the character poses to PNGs on a clean background (with a ground line
/// at the foot anchor and a vertical line through the navel) so the sprite assembly, anchoring, and
/// left/right flip can be eyeballed offscreen. Triggered by <c>--render-poses &lt;dir&gt;</c>.
/// </summary>
public static class PoseRenderTest
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var sprites = CharacterSprites.Load();
        if (sprites is null)
        {
            File.WriteAllText(Path.Combine(outDir, "FAILED.txt"), "CharacterSprites.Load() returned null");
            return;
        }
        File.WriteAllText(Path.Combine(outDir, "info.txt"),
            $"HalfWidth={sprites.HalfWidth} HeightAboveFeet={sprites.HeightAboveFeet}");

        const int W = 140, H = 190;
        var bg = new SolidColorBrush(Color.FromRgb(232, 232, 238));
        var groundPen = new Pen(new SolidColorBrush(Color.FromRgb(110, 110, 120)), 1);
        var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 200, 70, 70)), 1);

        foreach (var pose in new[] { "stand1", "walk1", "jump", "ladder", "rope" })
        {
            var p = sprites.GetPose(pose);
            if (p is null) continue;

            for (int f = 0; f < p.Frames.Count; f++)
            {
                foreach (bool flip in new[] { false, true })
                {
                    var rtb = new RenderTargetBitmap(new PixelSize(W, H), new Vector(96, 96));
                    using (var ctx = rtb.CreateDrawingContext())
                    {
                        ctx.DrawRectangle(bg, null, new Avalonia.Rect(0, 0, W, H));

                        double groundY = H - 20;
                        double centerX = W / 2.0;

                        ctx.DrawLine(groundPen, new Avalonia.Point(0, groundY), new Avalonia.Point(W, groundY));
                        ctx.DrawLine(centerPen, new Avalonia.Point(centerX, 0), new Avalonia.Point(centerX, H));

                        PetRenderer.DrawCharacter(ctx, sprites, pose, f, centerX, groundY, flip);
                    }
                    rtb.Save(Path.Combine(outDir, $"{pose}_f{f}_{(flip ? "R" : "L")}.png"));
                    rtb.Dispose();
                }
            }
        }
    }
}
