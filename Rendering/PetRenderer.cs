using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MaplePet.Engine;

namespace MaplePet.Rendering;

/// <summary>
/// Draws the pet as a layered sprite character: the Body + Head layers of the current animation frame,
/// pasted around the body navel. The navel is pinned to the world point <c>(CenterX, FeetY -
/// FootToNavel)</c> so the feet stay planted while the body animates. Footage is canonical
/// left-facing, so we mirror it horizontally (about the navel) when the pet faces right.
///
/// If the footage failed to load, it falls back to the original placeholder rectangle so the
/// overlay still shows something.
/// </summary>
public static class PetRenderer
{
    public static void Draw(DrawingContext ctx, PetController pet, CharacterSprites? sprites, CharacterAnimator? animator)
    {
        if (sprites is null || animator is null)
        {
            DrawFallback(ctx, pet);
            return;
        }

        var pose = sprites.GetPose(animator.Pose);
        if (pose is null || pose.Frames.Count == 0)
        {
            DrawFallback(ctx, pet);
            return;
        }

        // Canonical artwork faces left, so mirror for right-facing. Climbing is a back view that
        // doesn't depend on facing, so leave it unflipped.
        bool flip = pet.Facing > 0 && pet.State != PetState.Rope;

        // While an expression is active (e.g. during a drag), substitute it for the neutral face.
        var expression = animator.Expression is string name ? sprites.GetExpression(name) : null;

        DrawCharacter(ctx, sprites, animator.Pose, animator.FrameIndex, pet.CenterX, pet.FeetY, flip,
            expression, animator.ExpressionFrameIndex);
    }

    /// <summary>
    /// Paste one pose-frame's Body/Head layers with the feet planted at <paramref name="feetY"/> and
    /// the body navel centered at <paramref name="centerX"/>. When <paramref name="flipHorizontal"/>
    /// is true the artwork is mirrored about the navel (canonical footage faces left). Shared by the
    /// live pet and the offscreen pose tests.
    ///
    /// When <paramref name="faceExpression"/> is non-null and this frame has a face anchor, the chosen
    /// expression frame is drawn in the neutral face's place (same z-order), pinned so its origin lands
    /// on the frame's brow anchor.
    /// </summary>
    public static void DrawCharacter(DrawingContext ctx, CharacterSprites sprites, string poseName,
        int frameIndex, double centerX, double feetY, bool flipHorizontal,
        CharacterSprites.Expression? faceExpression = null, int expressionFrame = 0)
    {
        var pose = sprites.GetPose(poseName);
        if (pose is null || pose.Frames.Count == 0) return;

        int idx = frameIndex < 0 || frameIndex >= pose.Frames.Count ? 0 : frameIndex;
        var frame = pose.Frames[idx];

        // Pin the navel so this frame's foot line lands on feetY, and snap to whole pixels: the
        // artwork is native-resolution pixel art, so a fractional anchor (the pet moves by
        // speed*dt every frame) would otherwise resample and blur/shimmer the sprite — and round a
        // fractional anchor differently per facing, making the character hop ~1px when it turns.
        double navelX = Math.Round(centerX);
        double navelY = Math.Round(feetY - frame.FootOffset);

        // local (navel-relative) -> world: scale about the navel, then translate the navel to its
        // world position. Under Avalonia's row-vector convention, Scale*Translate applies scale first.
        var transform = Matrix.CreateScale(flipHorizontal ? -1.0 : 1.0, 1.0)
                        * Matrix.CreateTranslation(navelX, navelY);

        // Resolve the expression's current frame once (null if it has no usable frame).
        CharacterSprites.ExpressionFrame? exprFrame = null;
        if (faceExpression is { Frames.Count: > 0 } && frame.FaceAnchor is not null)
        {
            int ei = expressionFrame < 0 || expressionFrame >= faceExpression.Frames.Count ? 0 : expressionFrame;
            exprFrame = faceExpression.Frames[ei];
        }

        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        using (ctx.PushTransform(transform))
        {
            foreach (var layer in frame.Layers)
            {
                if (layer.IsFace && exprFrame is { } ef && frame.FaceAnchor is { } anchor)
                {
                    // Place the expression so its anchor point sits on the brow anchor — exactly how the
                    // neutral face's own offset was derived, so swapping is seamless across faces of
                    // different sizes and decorations.
                    ctx.DrawImage(ef.Image,
                        new Avalonia.Rect(anchor.X - ef.AnchorOffsetX, anchor.Y - ef.AnchorOffsetY, ef.Width, ef.Height));
                }
                else
                {
                    ctx.DrawImage(layer.Image, new Avalonia.Rect(layer.OffsetX, layer.OffsetY, layer.Width, layer.Height));
                }
            }
        }
    }

    // ---------------------------------------------------------------- Placeholder fallback
    private static readonly IBrush WalkBody = new SolidColorBrush(Color.FromArgb(235, 66, 135, 245));
    private static readonly IBrush StandBody = new SolidColorBrush(Color.FromArgb(235, 150, 180, 235));
    private static readonly IBrush RopeBody = new SolidColorBrush(Color.FromArgb(235, 80, 200, 120));
    private static readonly IBrush JumpBody = new SolidColorBrush(Color.FromArgb(235, 240, 150, 60));
    private static readonly IBrush Eye = Brushes.White;
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(235, 20, 40, 90)), 2);

    private static void DrawFallback(DrawingContext ctx, PetController pet)
    {
        var fill = pet.State switch
        {
            PetState.Stand => StandBody,
            PetState.Rope => RopeBody,
            PetState.Jump => JumpBody,
            _ => WalkBody,
        };
        var body = new Avalonia.Rect(pet.Pos.X, pet.Pos.Y, pet.Size.X, pet.Size.Y);
        ctx.DrawRectangle(fill, Outline, body, 5, 5);

        const double eyeSize = 5;
        double eyeX = pet.Facing >= 0 ? body.Right - eyeSize - 6 : body.Left + 6;
        double eyeY = body.Top + 8;
        ctx.DrawRectangle(Eye, null, new Avalonia.Rect(eyeX, eyeY, eyeSize, eyeSize));
    }
}
