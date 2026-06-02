using Avalonia.Controls;
using Avalonia.Media;
using MaplePet.Engine;
using MaplePet.Rendering;

namespace MaplePet.Views;

/// <summary>
/// The drawing surface that fills the overlay. It holds references to the latest world/pet
/// (updated by <see cref="PetWindow"/>) and redraws them every frame via <c>Render</c>.
/// Hit-testing is disabled; the whole overlay is click-through anyway.
/// </summary>
public sealed class GameView : Control
{
    public WorldGeometry? Geometry { get; set; }
    public World? World { get; set; }
    public PetController? Pet { get; set; }
    public bool ShowDebug { get; set; } = true;

    public GameView()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (ShowDebug && Geometry is { } geo && World is { } world)
            DebugOverlay.Draw(context, geo, world);

        if (Pet is { } pet)
        {
            if (ShowDebug) DebugOverlay.DrawPath(context, pet);
            PetRenderer.Draw(context, pet);
        }
    }
}
