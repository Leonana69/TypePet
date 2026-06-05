using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MaplePet.Platform.Windows;

/// <summary>
/// A low-level mouse hook that swallows a left-button press (and its matching release) when it lands
/// on the pet, so grabbing the pet doesn't also click the window behind it. This lets the overlay
/// stay permanently click-through (never an occluder, so hardware-accelerated video below it never
/// blacks out) while still "catching" the grab click.
///
/// Only the down (over the pet) and its matching up are eaten — mouse MOVES are always passed
/// through (blocking them would freeze the cursor, breaking the drag). Because a swallowed button
/// event is NOT seen by GetAsyncKeyState, the hook also tracks the physical left-button state itself
/// (it sees every event before deciding to swallow), and the pet's drag loop reads that via
/// <see cref="LeftButtonDown"/>. The callback must be fast and must never throw (a low-level hook
/// sits in the path of all system mouse input).
/// </summary>
public sealed class MouseClickBlocker : IDisposable
{
    private readonly HOOKPROC _proc; // kept alive so the GC can't collect the native thunk
    private UnhookWindowsHookExSafeHandle? _hook;
    private FreeLibrarySafeHandle? _module;

    // Pet's clickable rect in physical screen px, published from the UI thread each tick.
    private volatile bool _enabled;
    private volatile int _left, _top, _right, _bottom;
    // The speech-bubble link's clickable rect (physical px); a press here is swallowed too, so the click
    // opens the link instead of reaching the window behind.
    private volatile bool _linkEnabled;
    private volatile int _ll, _lt, _lr, _lb;
    private volatile bool _leftDown; // physical left-button state seen by the hook (even when swallowed)
    private volatile bool _swallowedDown; // latch: we ate the down, so eat the matching up

    /// <summary>Left-button state observed by the hook — reliable even when the click is swallowed
    /// (unlike GetAsyncKeyState, which doesn't see a hook-blocked button event).</summary>
    public bool LeftButtonDown => _leftDown;

    public MouseClickBlocker() => _proc = HookProc;

    /// <summary>Install the system-wide low-level mouse hook. Call once on the UI thread.</summary>
    public void Install()
    {
        if (_hook is not null) return;
        _module = PInvoke.GetModuleHandle((string?)null);
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _proc, _module, 0);
    }

    /// <summary>Publish the pet's clickable rect (physical px). When disabled, no new press is eaten.</summary>
    public void SetPetRect(bool enabled, int left, int top, int right, int bottom)
    {
        _left = left; _top = top; _right = right; _bottom = bottom;
        _enabled = enabled;
    }

    /// <summary>Publish the speech-bubble link's clickable rect (physical px). When disabled, no press
    /// over it is eaten.</summary>
    public void SetLinkRect(bool enabled, int left, int top, int right, int bottom)
    {
        _ll = left; _lt = top; _lr = right; _lb = bottom;
        _linkEnabled = enabled;
    }

    /// <summary>Forget any in-flight swallowed press so the NEXT button-up is passed through instead of
    /// eaten. Used when the overlay is hidden mid-press (a fullscreen app took over): otherwise the
    /// latch would swallow a release that belongs to the app behind us.</summary>
    public void ResetSwallow() => _swallowedDown = false;

    private unsafe LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                uint msg = (uint)wParam.Value;
                if (msg == PInvoke.WM_LBUTTONDOWN)
                {
                    _leftDown = true;
                    var data = *(MSLLHOOKSTRUCT*)lParam.Value;
                    bool injected = (data.flags & PInvoke.LLMHF_INJECTED) != 0;
                    bool overPet = _enabled
                        && data.pt.X >= _left && data.pt.X < _right
                        && data.pt.Y >= _top && data.pt.Y < _bottom;
                    bool overLink = _linkEnabled
                        && data.pt.X >= _ll && data.pt.X < _lr
                        && data.pt.Y >= _lt && data.pt.Y < _lb;
                    if (!injected && (overPet || overLink))
                    {
                        _swallowedDown = true;
                        return (LRESULT)1; // eat the press on the pet/link so the window behind doesn't get it
                    }
                }
                else if (msg == PInvoke.WM_LBUTTONUP)
                {
                    _leftDown = false;
                    if (_swallowedDown)
                    {
                        _swallowedDown = false;
                        return (LRESULT)1; // eat the matching release wherever it lands
                    }
                }
            }
        }
        catch
        {
            // A low-level hook must never throw — always fall through to pass the event on.
        }
        return PInvoke.CallNextHookEx(null, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        _module?.Dispose();
        _module = null;
    }
}
