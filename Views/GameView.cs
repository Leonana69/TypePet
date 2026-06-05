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
    public CharacterSprites? Sprites { get; set; }
    public CharacterAnimator? Animator { get; set; }
    public string? Speech { get; set; }          // active speech-bubble text (control API's Say), or null
    public string? SpeechLink { get; set; }      // optional clickable link label drawn in the bubble, or null
    public Avalonia.Media.IImage? SpeechImage { get; set; } // optional character image drawn atop the bubble
    // The clickable link's bounds in logical overlay px, recomputed each Render (null when no link is drawn).
    // PetWindow reads this to publish a hit rect to the input layer.
    public MaplePet.Engine.Rect? SpeechLinkRect { get; private set; }
    public bool ShowDebug { get; set; } = false; // driven by Settings.ShowOverlay via PetWindow

    public GameView()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // The whole frame is guarded: this runs in Avalonia's render pass (the game loop), so an
        // exception here — e.g. text shaping a pathological speech-bubble glyph — would otherwise be
        // unhandled and force-close the app. A dropped frame is far better than a crash.
        SpeechLinkRect = null; // recomputed below only when a link line is actually drawn
        try
        {
            if (ShowDebug && Geometry is { } geo && World is { } world)
                DebugOverlay.Draw(context, geo, world);

            if (Pet is { } pet)
            {
                if (ShowDebug) DebugOverlay.DrawPath(context, pet);
                PetRenderer.Draw(context, pet, Sprites, Animator);

                if (!string.IsNullOrEmpty(Speech))
                {
                    // Anchor the bubble at the top-center of the drawn pet (falls back to the physics box
                    // when footage didn't load).
                    double topY = Sprites is { } s ? pet.FeetY - s.HeightAboveFeet : pet.Pos.Y;
                    SpeechLinkRect = SpeechBubble.Draw(context, Speech!, SpeechLink, SpeechImage, pet.CenterX, topY,
                        new MaplePet.Engine.Rect(0, 0, Bounds.Width, Bounds.Height));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MaplePet] render failed: {ex.Message}");
        }
    }
}
