using System;
using MaplePet.Engine;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Windows;

/// <summary>
/// Windows pet input. The overlay stays permanently click-through (so it never occludes
/// hardware-accelerated video below it), so drag/click can't come from Avalonia pointer events — it's
/// driven by polling the global cursor + left button every tick. The grab click itself is "caught" by a
/// low-level mouse hook (<see cref="MouseClickBlocker"/>) that eats the press/release only when it lands
/// on the pet, so it doesn't reach the window behind. Double-click is synthesized from the polled
/// press/release edges (the overlay never receives a real double-click). All of this used to live in
/// PetWindow; it now sits behind <see cref="IPetInput"/> so PetWindow speaks only logical pixels.
/// </summary>
public sealed class WindowsPetInput : IPetInput
{
    private readonly MouseClickBlocker _clickBlocker = new();
    private PetInputCallbacks _cb = null!;
    private ScreenSpace _screen = new(0, 0, 1);

    private bool _dragging;     // a grab is in progress (mirrors the pet's drag state, our source of truth)
    private bool _lmbPrev;      // left button state on the previous tick

    // Click/double-click synthesis on the (click-through) pet, derived from the polled drag edges.
    private long _downTickMs;     // when the current press on the pet began
    private Vec2 _downCursor;     // cursor at that press, to tell an in-place click from a drag
    private bool _movedThisPress; // the pointer ever moved past the click threshold during this press
    private long _lastClickUpMs;  // when the previous in-place click released (for double-click pairing)
    private Vec2 _lastClickPos;
    private const double ClickMaxMoveLogicalPx = 6; // a press+release that moves less than this is a "click"
    private const long ClickMaxHoldMs = 600;        // ...and is released within this long (else it's a hold)

    public void Start(PetInputCallbacks callbacks)
    {
        _cb = callbacks;
        _clickBlocker.Install();
    }

    public void Tick(ScreenSpace screen, bool petVisible, double left, double top, double right, double bottom)
    {
        _screen = screen;

        // 1. Publish the pet's clickable rect to the hook in physical screen px (inverse of the
        //    cursor->logical conversion), so a grab click landing on the pet is swallowed.
        if (petVisible)
            _clickBlocker.SetPetRect(true,
                (int)Math.Floor(left * _screen.Scale + _screen.OriginX),
                (int)Math.Floor(top * _screen.Scale + _screen.OriginY),
                (int)Math.Ceiling(right * _screen.Scale + _screen.OriginX),
                (int)Math.Ceiling(bottom * _screen.Scale + _screen.OriginY));
        else
            _clickBlocker.SetPetRect(false, 0, 0, 0, 0);

        // 2. Poll the cursor + button to drive dragging. Use the hook's button state: a click swallowed
        //    by the hook (over the pet) isn't seen by GetAsyncKeyState, so the poll would miss the press.
        bool lmb = _clickBlocker.LeftButtonDown;
        bool haveCursor = TryCursorLogical(out var cursor);
        bool overPet = petVisible && haveCursor
            && cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;

        if (!_dragging && lmb && !_lmbPrev && overPet)
        {
            _downTickMs = Environment.TickCount64; // press time + point, to tell an in-place click from a drag
            _downCursor = cursor;
            _movedThisPress = false;
            _dragging = true;
            _cb.OnDragBegin(cursor);
        }

        if (_dragging)
        {
            if (lmb)
            {
                if (haveCursor)
                {
                    if (Dist(cursor, _downCursor) > ClickMaxMoveLogicalPx) _movedThisPress = true;
                    _cb.OnDragMove(cursor);
                }
            }
            else
            {
                if (haveCursor) DetectDoubleClick(cursor);
                _dragging = false;
                _cb.OnDragEnd();
            }
        }
        _lmbPrev = lmb;
    }

    public void CancelActiveGesture()
    {
        // Drop the click hook's swallowed-press latch (so a button-up meant for a fullscreen app behind
        // us isn't eaten) and forget the per-drag input edges so a stale drag can't resume on re-show.
        _clickBlocker.SetPetRect(false, 0, 0, 0, 0);
        _clickBlocker.ResetSwallow();
        _lmbPrev = false;
        _dragging = false;
    }

    /// <summary>
    /// On releasing the pet, decide whether it was an in-place click (barely moved) and, if a second
    /// such click lands within the OS double-click window, request the say-input bar. A drag (moved past
    /// the threshold) resets the pairing so it never counts as a click.
    /// </summary>
    private void DetectDoubleClick(Vec2 up)
    {
        long now = Environment.TickCount64;
        bool isClick = !_movedThisPress
            && Dist(up, _downCursor) <= ClickMaxMoveLogicalPx
            && now - _downTickMs <= ClickMaxHoldMs;
        if (!isClick)
        {
            _lastClickUpMs = 0;
            return;
        }

        if (_lastClickUpMs != 0
            && now - _lastClickUpMs <= WindowsInterop.DoubleClickTimeMs()
            && Dist(up, _lastClickPos) <= ClickMaxMoveLogicalPx * 2)
        {
            _lastClickUpMs = 0; // consume the pair
            _cb.OnSayRequested();
        }
        else
        {
            _lastClickUpMs = now; // first click of a potential pair
            _lastClickPos = up;
        }
    }

    private bool TryCursorLogical(out Vec2 logical)
    {
        logical = default;
        if (!WindowsInterop.TryGetCursorPos(out int px, out int py)) return false;
        logical = new Vec2((px - _screen.OriginX) / _screen.Scale, (py - _screen.OriginY) / _screen.Scale);
        return true;
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void Dispose() => _clickBlocker.Dispose();
}
