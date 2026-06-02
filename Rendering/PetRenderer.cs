using Avalonia.Media;
using MaplePet.Engine;

namespace MaplePet.Rendering;

/// <summary>
/// Draws the pet. For now it's a simple rounded rectangle with an eye that shows facing
/// direction — a placeholder to be replaced by sprite-sheet frames in M9.
/// </summary>
public static class PetRenderer
{
    // Tinted per state so the walk/climb/fall behavior is visible at a glance.
    private static readonly IBrush WalkBody = new SolidColorBrush(Color.FromArgb(235, 66, 135, 245));  // blue
    private static readonly IBrush ClimbBody = new SolidColorBrush(Color.FromArgb(235, 80, 200, 120)); // green
    private static readonly IBrush FallBody = new SolidColorBrush(Color.FromArgb(235, 240, 150, 60));  // orange
    private static readonly IBrush Eye = Brushes.White;
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(235, 20, 40, 90)), 2);

    public static void Draw(DrawingContext ctx, PetController pet)
    {
        var fill = pet.State switch
        {
            PetState.Climbing => ClimbBody,
            PetState.Falling => FallBody,
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
