using System;
using System.Runtime.InteropServices;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>
/// macOS global say-bar hotkey via Carbon <c>RegisterEventHotKey</c> — permission-free for a registered
/// chord (unlike a CGEventTap, it needs no Accessibility / Input Monitoring grant). A single application
/// event handler catches the hotkey-pressed event and raises <see cref="Triggered"/> on the main/UI
/// thread (where Avalonia pumps the run loop). The interface passes the Windows virtual-key code (the
/// app's canonical key id); this maps it to the Carbon physical key code.
/// </summary>
public sealed class MacGlobalHotkey : IGlobalHotkey
{
    public bool IsSupported => true;
    public event Action? Triggered;

    // Carbon modifier masks.
    private const uint CmdKey = 0x0100, ShiftKey = 0x0200, OptionKey = 0x0800, ControlKey = 0x1000;

    private readonly MacNative.EventHandlerProc _proc;
    private IntPtr _handlerRef;
    private IntPtr _hotKeyRef;
    private bool _installed;
    private bool _enabled;

    public MacGlobalHotkey() => _proc = HandleHotKey; // hold the delegate so the native thunk stays alive

    public void Install()
    {
        if (_installed) return;
        var target = MacNative.GetApplicationEventTarget();
        if (target == IntPtr.Zero) return;

        var spec = new[]
        {
            new MacNative.EventTypeSpec { EventClass = MacNative.kEventClassKeyboard, EventKind = MacNative.kEventHotKeyPressed }
        };
        int status = MacNative.InstallEventHandler(
            target, Marshal.GetFunctionPointerForDelegate(_proc), 1, spec, IntPtr.Zero, out _handlerRef);
        _installed = status == 0;
    }

    public void SetHotkey(int virtualKey, bool ctrl, bool alt, bool shift, bool meta)
    {
        UnregisterCurrent();
        if (VkToCarbon(virtualKey) is not int code) { _enabled = false; return; }

        uint mods = 0;
        if (ctrl) mods |= ControlKey;
        if (alt) mods |= OptionKey;
        if (shift) mods |= ShiftKey;
        if (meta) mods |= CmdKey;

        var id = new MacNative.EventHotKeyID { Signature = 0x4D506574 /* 'MPet' */, Id = 1 };
        int status = MacNative.RegisterEventHotKey(
            (uint)code, mods, id, MacNative.GetApplicationEventTarget(), 0, out _hotKeyRef);
        _enabled = status == 0 && _hotKeyRef != IntPtr.Zero;
    }

    // On macOS the registration itself captures the chord system-wide, so disabling must UNREGISTER it
    // (not merely stop acting on it) — otherwise we'd swallow the key without using it.
    public void Disable()
    {
        _enabled = false;
        UnregisterCurrent();
    }

    private int HandleHotKey(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        try { if (_enabled) Triggered?.Invoke(); } catch { /* a native callback must never throw */ }
        return 0; // noErr — consume the hotkey
    }

    private void UnregisterCurrent()
    {
        if (_hotKeyRef != IntPtr.Zero)
        {
            MacNative.UnregisterEventHotKey(_hotKeyRef);
            _hotKeyRef = IntPtr.Zero;
        }
    }

    public void Dispose() => UnregisterCurrent();

    // ---- Windows VK -> Carbon physical key code, for the keys HotkeyGesture supports (letters,
    //      digits, F1-F20, Space). Carbon codes are physical positions, not alphabetical. ----
    private static readonly int[] LetterCarbon =
        { 0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6 }; // A..Z
    private static readonly int[] DigitCarbon = { 29, 18, 19, 20, 21, 23, 22, 26, 28, 25 };                // 0..9
    private static readonly int[] FCarbon =
        { 122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90 };    // F1..F20

    private static int? VkToCarbon(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return LetterCarbon[vk - 0x41];     // 'A'..'Z'
        if (vk >= 0x30 && vk <= 0x39) return DigitCarbon[vk - 0x30];      // '0'..'9'
        if (vk >= 0x70 && vk <= 0x70 + 19) return FCarbon[vk - 0x70];     // F1..F20 (F21-F24 unmapped)
        if (vk == 0x20) return 49;                                        // Space (kVK_Space)
        return null;
    }
}
