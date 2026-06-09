using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TypePet.Engine;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Mac;

/// <summary>
/// macOS pet input — the permission-free grab. The overlay is click-through (ignoresMouseEvents=true) by
/// default, so clicks pass to the app behind it. Each game-loop tick the global cursor is read
/// permission-free (CGEventGetLocation) and, while it's over the pet (plus a hysteresis margin to beat
/// the toggle-vs-press race), the overlay is flipped interactive so Avalonia delivers native
/// PointerPressed/Moved/Released — which become the drag gestures. A mid-drag latch keeps it interactive
/// so a fast drag that briefly leaves the box doesn't drop the button. No low-level hook, no event tap,
/// no TCC permission. Double-click (ClickCount==2) opens the say bar.
/// </summary>
public sealed class MacPetInput : IPetInput
{
    private const double HitMargin = 12;        // become interactive a bit before the cursor reaches the box
    private const double LinkClickMaxMove = 6;  // a link press+release that moves less than this is a "click"

    private readonly Window _window;
    private readonly IntPtr _win; // NSWindow
    private PetInputCallbacks _cb = null!;
    private ScreenSpace _screen = new(0, 0, 1);

    private bool _interactive = true; // tracks "ignoresMouseEvents == false"; starts true so the first
                                      // SetInteractive(false) actually issues the click-through call
    private bool _dragging;
    private HitRect _pet;             // the pet's clickable rect (logical px), updated each Tick
    private HitRect _link;            // the speech-bubble link rect (logical px), updated each Tick
    private bool _linkArmed;          // a press landed on the link; fire on the matching in-place release
    private Vec2 _linkDownPos;        // cursor at that press, to tell an in-place click from a drag-off

    public MacPetInput(Window overlay)
    {
        _window = overlay;
        _win = MacNative.ToNSWindow(overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    public void Start(PetInputCallbacks callbacks)
    {
        _cb = callbacks;
        const RoutingStrategies routes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        _window.AddHandler(InputElement.PointerPressedEvent, OnPressed, routes, handledEventsToo: true);
        _window.AddHandler(InputElement.PointerMovedEvent, OnMoved, routes, handledEventsToo: true);
        _window.AddHandler(InputElement.PointerReleasedEvent, OnReleased, routes, handledEventsToo: true);
        SetInteractive(false); // click-through to start; Tick re-evaluates each frame
    }

    public void Tick(ScreenSpace screen, HitRect pet, HitRect link)
    {
        _screen = screen;
        _pet = pet;
        _link = link;
        if (_win == IntPtr.Zero) return;

        bool wantInteractive = _dragging || _linkArmed; // latch through an active drag OR link press
        if (!wantInteractive && TryCursorLogical(out var c))
            wantInteractive = Near(pet, c) || Near(link, c); // interactive over the pet OR the bubble link
        SetInteractive(wantInteractive);
    }

    /// <summary>True when <paramref name="c"/> is within <paramref name="r"/> plus the hysteresis margin
    /// (so the overlay flips interactive just before the cursor reaches the box).</summary>
    private static bool Near(HitRect r, Vec2 c)
        => r.Visible && c.X >= r.Left - HitMargin && c.X <= r.Right + HitMargin
                     && c.Y >= r.Top - HitMargin && c.Y <= r.Bottom + HitMargin;

    public void CancelActiveGesture()
    {
        _dragging = false;
        _linkArmed = false;
        SetInteractive(false); // back to click-through; PetWindow already ended the pet's drag
    }

    // The pointer handlers are registered for Tunnel|Bubble + handledEventsToo, so each fires TWICE per
    // physical event (the window is both the first tunnel target and the last bubble target). The drag path
    // tolerates that (begin/move/end are idempotent); the link path must not double-open the URL, so it
    // ARMS on press and fires once on release by clearing _linkArmed before invoking the callback — the
    // second invocation then sees it disarmed and no-ops.
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(_window);
        var pv = new Vec2(p.X, p.Y);

        // A press on the bubble link arms it (the URL opens on the in-place release, so a press-and-drag-off
        // cancels) and must NOT start a pet drag.
        if (_link.Contains(pv))
        {
            _linkArmed = true;
            _linkDownPos = pv;
            e.Pointer.Capture(_window); // keep the release coming even if the interactive toggle flips
            e.Handled = true;
            return;
        }

        // Only start a drag when the press is actually over the pet (mirrors Windows' overPet && !overLink):
        // the interactive zone extends a margin past the pet/link, so a press in that band must do nothing.
        if (!_pet.Contains(pv)) return;

        e.Pointer.Capture(_window); // keep move/release coming even if the toggle flips mid-drag
        _dragging = true;
        _cb.OnDragBegin(pv);
        if (e.ClickCount == 2) _cb.OnSayRequested();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return; // an armed link press ignores moves; the release decides whether it counts
        var p = e.GetPosition(_window);
        _cb.OnDragMove(new Vec2(p.X, p.Y));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_linkArmed)
        {
            _linkArmed = false; // disarm BEFORE firing, so the second (bubble-pass) invocation no-ops
            e.Pointer.Capture(null);
            var p = e.GetPosition(_window);
            var pv = new Vec2(p.X, p.Y);
            if (_link.Contains(pv) && Dist(pv, _linkDownPos) <= LinkClickMaxMove) _cb.OnLinkActivated();
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        _cb.OnDragEnd();
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private bool TryCursorLogical(out Vec2 logical)
    {
        logical = default;
        var ev = MacNative.CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero) return false;
        var p = MacNative.CGEventGetLocation(ev);
        MacNative.CFRelease(ev);
        logical = new Vec2((p.X - _screen.OriginX) / _screen.Scale, (p.Y - _screen.OriginY) / _screen.Scale);
        return true;
    }

    private void SetInteractive(bool interactive)
    {
        if (interactive == _interactive || _win == IntPtr.Zero) return;
        _interactive = interactive;
        MacNative.SendBool(_win, "setIgnoresMouseEvents:", !interactive);
    }

    public void Dispose()
    {
        _window.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        _window.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        _window.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
    }
}
