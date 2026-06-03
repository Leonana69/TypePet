using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MaplePet.Platform.Windows;

/// <summary>
/// A system-wide low-level keyboard hook (WH_KEYBOARD_LL) that raises <see cref="Triggered"/> when a
/// configured global hotkey (a virtual-key code + required modifier set) is pressed — used to pop up
/// the "say" input bar from anywhere. Modelled directly on <see cref="MouseClickBlocker"/>: installed
/// on the UI thread (so the callback is delivered there and <see cref="Triggered"/> may touch the UI),
/// the hook delegate kept alive in a field, and a callback that must never throw or block.
///
/// The matched chord is swallowed (its key never reaches the focused app, like Spotlight's Cmd+Space),
/// and a held-latch makes it fire once per physical press rather than on every auto-repeat. Only the
/// single configured key is inspected; no keystrokes are logged or stored.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private readonly HOOKPROC _proc; // kept alive so the GC can't collect the native thunk
    private UnhookWindowsHookExSafeHandle? _hook;
    private FreeLibrarySafeHandle? _module;

    // Configured chord. Published from the UI thread; read in the hook callback, which is delivered on
    // that same (installing) thread, so no cross-thread synchronization is needed beyond _enabled.
    private volatile bool _enabled;
    private int _vk;
    private bool _needCtrl, _needAlt, _needShift, _needWin;
    private bool _chordHeld; // latch: we matched & swallowed the keydown; swallow repeats + the matching up
    private int _heldVk;     // the exact vk we swallowed, so we still swallow its up after a disable/rebind

    /// <summary>Raised on the UI thread when the configured hotkey is pressed. Keep the handler trivial
    /// (it runs inside the system-wide keyboard hook) — defer real work via Dispatcher.UIThread.Post.</summary>
    public event Action? Triggered;

    public HotkeyListener() => _proc = HookProc;

    /// <summary>Install the system-wide low-level keyboard hook. Call once on the UI thread.</summary>
    public void Install()
    {
        if (_hook is not null) return;
        _module = PInvoke.GetModuleHandle((string?)null);
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _proc, _module, 0);
        if (_hook is null || _hook.IsInvalid)
        {
            // The friendly overload returns a non-null-but-invalid handle on failure; drop it so a later
            // Install() can retry, and trace it (the hotkey is otherwise silently inert).
            System.Diagnostics.Debug.WriteLine($"[MaplePet] keyboard hook install failed: {Marshal.GetLastWin32Error()}");
            _hook?.Dispose();
            _hook = null;
        }
    }

    /// <summary>Set the active chord (virtual-key code + required modifiers). A non-positive
    /// <paramref name="virtualKey"/> disables matching without uninstalling the hook.</summary>
    public void SetHotkey(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        _vk = virtualKey;
        _needCtrl = ctrl; _needAlt = alt; _needShift = shift; _needWin = win;
        _chordHeld = false;
        _enabled = virtualKey > 0;
    }

    /// <summary>Stop matching the hotkey (the hook stays installed but inert).</summary>
    public void Disable() => _enabled = false;

    private unsafe LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                uint msg = (uint)wParam.Value;
                uint vk = ((KBDLLHOOKSTRUCT*)lParam.Value)->vkCode;

                // Always finish swallowing a chord we already started — even if it was disabled or
                // rebound while the key is still physically held — so the focused app never gets a
                // lone key-up without its key-down (mirrors MouseClickBlocker's latch).
                if (msg == PInvoke.WM_KEYUP || msg == PInvoke.WM_SYSKEYUP)
                {
                    if (_chordHeld && vk == (uint)_heldVk)
                    {
                        _chordHeld = false;
                        return (LRESULT)1;
                    }
                }
                // WM_SYSKEYDOWN also fires here so Alt-combos (which arrive as "system" keys) aren't missed.
                else if (_enabled && (msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN) && vk == (uint)_vk)
                {
                    if (_chordHeld) return (LRESULT)1;           // swallow auto-repeat while still held
                    if (ModifiersMatch())
                    {
                        _chordHeld = true;
                        _heldVk = _vk;                           // remember exactly what we swallowed
                        Triggered?.Invoke();                     // on the UI thread; handler must defer real work
                        return (LRESULT)1;                       // swallow so the key doesn't also reach the app
                    }
                    // key matched but modifiers didn't — a plain keypress; let it through
                }
            }
        }
        catch
        {
            // A low-level hook must never throw — always fall through to pass the event on.
        }
        return PInvoke.CallNextHookEx(null, nCode, wParam, lParam);
    }

    // Exact match: every modifier is either required-and-down or not-required-and-up, so e.g.
    // Ctrl+Alt+S won't also fire when Shift is held. A Ctrl+Alt chord additionally declines AltGr
    // (LeftCtrl+RightAlt on international layouts), so it doesn't eat the user's accented characters.
    private bool ModifiersMatch()
    {
        if (_needCtrl && _needAlt && WindowsInterop.LooksLikeAltGr()) return false;
        return WindowsInterop.IsCtrlDown() == _needCtrl &&
               WindowsInterop.IsAltDown() == _needAlt &&
               WindowsInterop.IsShiftDown() == _needShift &&
               WindowsInterop.IsWinDown() == _needWin;
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        _module?.Dispose();
        _module = null;
    }
}
