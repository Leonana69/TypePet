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
    /// to logical mapping; <paramref name="pet"/> is the pet's clickable rect and <paramref name="link"/>
    /// the optional speech-bubble link rect, both in LOGICAL overlay pixels (<see cref="HitRect.Visible"/>
    /// = false when absent). Windows polls the cursor + button here and raises gestures, and publishes the
    /// rects (converted to physical px) to its click hook; macOS uses them + the cursor to toggle
    /// click-through.</summary>
    void Tick(ScreenSpace screen, HitRect pet, HitRect link);

    /// <summary>Tear down any in-progress grab (the overlay was hidden mid-press). Windows: forget the
    /// hook's swallowed press and reset the drag edges; macOS: cancel the active gesture.</summary>
    void CancelActiveGesture();
}

/// <summary>An axis-aligned clickable region in LOGICAL overlay pixels, or <see cref="Visible"/> = false
/// when there's nothing to hit. Used for both the pet's body and the speech-bubble link.</summary>
public readonly record struct HitRect(bool Visible, double Left, double Top, double Right, double Bottom)
{
    public static readonly HitRect None = new(false, 0, 0, 0, 0);
    public bool Contains(Vec2 p)
        => Visible && p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}

/// <summary>UI-thread callbacks describing pet interaction, all in LOGICAL overlay pixels. The platform
/// owns the raw-input bookkeeping (button edges, double-click timing, hook vs native events); PetWindow
/// just reacts by driving the pet.</summary>
public sealed record PetInputCallbacks(
    Action<Vec2> OnDragBegin,   // a press landed on the pet (cursor in logical px)
    Action<Vec2> OnDragMove,    // pointer moved while grabbing
    Action       OnDragEnd,     // released
    Action       OnSayRequested,// an in-place double-click on the pet (opens the say bar)
    Action       OnLinkActivated// a single click on the speech-bubble link (opens its URL)
);
