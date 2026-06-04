using System;
using MaplePet.Engine;

namespace MaplePet.Platform.Abstractions;

/// <summary>
/// Pet interaction delivered as GESTURES rather than raw cursor/button state. This is the seam that
/// lets the two very different input models fit behind one interface: Windows synthesizes these gestures
/// from a global cursor poll + a low-level mouse hook; macOS raises them from native Avalonia pointer
/// events (toggling the overlay interactive only while the cursor is over the pet). PetWindow consumes
/// the gestures and never polls the OS directly.
/// </summary>
public interface IPetInput : IDisposable
{
    /// <summary>Begin delivering input. The callbacks are always invoked on the UI thread, in LOGICAL
    /// overlay pixels (the overlay's 0,0..Width,Height space).</summary>
    void Start(PetInputCallbacks callbacks);

    /// <summary>Called once per game-loop tick. <paramref name="screen"/> is the current physical/point
    /// to logical mapping; the pet's clickable rect is given in LOGICAL overlay pixels (or
    /// <paramref name="petVisible"/> = false when the pet is hidden / has no hit area). Windows polls the
    /// cursor + button here and raises gestures, and publishes the rect (converted to physical px) to its
    /// click hook; macOS uses the rect + cursor to toggle click-through.</summary>
    void Tick(ScreenSpace screen, bool petVisible, double left, double top, double right, double bottom);

    /// <summary>Tear down any in-progress grab (the overlay was hidden mid-press). Windows: forget the
    /// hook's swallowed press and reset the drag edges; macOS: cancel the active gesture.</summary>
    void CancelActiveGesture();
}

/// <summary>UI-thread callbacks describing pet interaction, all in LOGICAL overlay pixels. The platform
/// owns the raw-input bookkeeping (button edges, double-click timing, hook vs native events); PetWindow
/// just reacts by driving the pet.</summary>
public sealed record PetInputCallbacks(
    Action<Vec2> OnDragBegin,   // a press landed on the pet (cursor in logical px)
    Action<Vec2> OnDragMove,    // pointer moved while grabbing
    Action       OnDragEnd,     // released
    Action       OnSayRequested // an in-place double-click on the pet (opens the say bar)
);
