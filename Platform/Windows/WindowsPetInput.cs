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

    private HitRect _link;          // the speech-bubble link rect (logical px), published each tick
    private bool _linkArmed;        // a press landed on the link; waiting for an in-place release to activate
    private Vec2 _linkDownCursor;   // cursor at that press, to tell an in-place click from a drag-off
    private long _lastLinkFireMs;   // when the link last opened, to debounce a double-click into one open

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

    public void Tick(ScreenSpace screen, HitRect pet, HitRect link)
    {
        _screen = screen;
        _link = link;

        // 1. Publish the pet + link clickable rects to the hook in physical screen px (inverse of the
        //    cursor->logical conversion), so a press landing on either is swallowed (never reaches the
        //    window behind).
        if (pet.Visible)
            _clickBlocker.SetPetRect(true, PxL(pet.Left), PxT(pet.Top), PxR(pet.Right), PxB(pet.Bottom));
        else
            _clickBlocker.SetPetRect(false, 0, 0, 0, 0);

        if (link.Visible)
            _clickBlocker.SetLinkRect(true, PxL(link.Left), PxT(link.Top), PxR(link.Right), PxB(link.Bottom));
        else
            _clickBlocker.SetLinkRect(false, 0, 0, 0, 0);

        // 2. Poll the cursor + button to drive dragging. Use the hook's button state: a click swallowed
        //    by the hook (over the pet/link) isn't seen by GetAsyncKeyState, so the poll would miss the press.
        bool lmb = _clickBlocker.LeftButtonDown;
        bool haveCursor = TryCursorLogical(out var cursor);
        bool overPet = pet.Visible && haveCursor
            && cursor.X >= pet.Left && cursor.X <= pet.Right && cursor.Y >= pet.Top && cursor.Y <= pet.Bottom;
        bool overLink = haveCursor && link.Contains(cursor);

        // Pet drag start. The link takes precedence, so clicking it never grabs/moves the pet.
        if (!_dragging && lmb && !_lmbPrev && overPet && !overLink)
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
        else
        {
            // Link click: a single in-place press+release on the bubble link opens its URL. A debounce
            // after firing folds a double-click (two press+releases) into a single open.
            if (!_linkArmed && lmb && !_lmbPrev && overLink
                && Environment.TickCount64 - _lastLinkFireMs > WindowsInterop.DoubleClickTimeMs())
            {
                _linkArmed = true;
                _linkDownCursor = cursor;
            }
            else if (_linkArmed && !lmb)
            {
                if (overLink && haveCursor && Dist(cursor, _linkDownCursor) <= ClickMaxMoveLogicalPx)
                {
                    _cb.OnLinkActivated();
                    _lastLinkFireMs = Environment.TickCount64;
                }
                _linkArmed = false;
            }
        }
        _lmbPrev = lmb;
    }

    // Logical overlay px -> physical screen px (the hook works in physical px). Floor lefts/tops, ceil
    // rights/bottoms so the swallowed rect fully covers the drawn region.
    private int PxL(double v) => (int)Math.Floor(v * _screen.Scale + _screen.OriginX);
    private int PxT(double v) => (int)Math.Floor(v * _screen.Scale + _screen.OriginY);
    private int PxR(double v) => (int)Math.Ceiling(v * _screen.Scale + _screen.OriginX);
    private int PxB(double v) => (int)Math.Ceiling(v * _screen.Scale + _screen.OriginY);

    public void CancelActiveGesture()
    {
        // Drop the click hook's swallowed-press latch (so a button-up meant for a fullscreen app behind
        // us isn't eaten) and forget the per-drag input edges so a stale drag can't resume on re-show.
        _clickBlocker.SetPetRect(false, 0, 0, 0, 0);
        _clickBlocker.SetLinkRect(false, 0, 0, 0, 0);
        _clickBlocker.ResetSwallow();
        _lmbPrev = false;
        _dragging = false;
        _linkArmed = false;
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
