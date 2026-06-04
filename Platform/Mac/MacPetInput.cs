using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MaplePet.Engine;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

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
    private const double HitMargin = 12; // become interactive a bit before the cursor reaches the box

    private readonly Window _window;
    private readonly IntPtr _win; // NSWindow
    private PetInputCallbacks _cb = null!;
    private ScreenSpace _screen = new(0, 0, 1);

    private bool _interactive = true; // tracks "ignoresMouseEvents == false"; starts true so the first
                                      // SetInteractive(false) actually issues the click-through call
    private bool _dragging;

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

    public void Tick(ScreenSpace screen, bool petVisible, double left, double top, double right, double bottom)
    {
        _screen = screen;
        if (_win == IntPtr.Zero) return;

        bool wantInteractive = _dragging; // mid-drag latch
        if (!wantInteractive && petVisible && TryCursorLogical(out var c))
            wantInteractive = c.X >= left - HitMargin && c.X <= right + HitMargin
                           && c.Y >= top - HitMargin && c.Y <= bottom + HitMargin;
        SetInteractive(wantInteractive);
    }

    public void CancelActiveGesture()
    {
        _dragging = false;
        SetInteractive(false); // back to click-through; PetWindow already ended the pet's drag
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(_window);
        e.Pointer.Capture(_window); // keep move/release coming even if the toggle flips mid-drag
        _dragging = true;
        _cb.OnDragBegin(new Vec2(p.X, p.Y));
        if (e.ClickCount == 2) _cb.OnSayRequested();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_window);
        _cb.OnDragMove(new Vec2(p.X, p.Y));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        _cb.OnDragEnd();
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
