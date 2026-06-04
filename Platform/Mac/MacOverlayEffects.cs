using System;
using Avalonia.Threading;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>
/// macOS overlay effects via objc_msgSend against the NSWindow Avalonia owns. Makes the overlay:
/// click-through (ignoresMouseEvents), present on every Space and floating over fullscreen apps
/// (collectionBehavior), and above everything (screen-saver window level). Avalonia can clobber the
/// hand-set level when it re-applies its own window management, so a timer re-asserts both the level and
/// the collection behaviour (the macOS analogue of the Windows RaiseToTop timer — it is NOT a no-op).
/// </summary>
public sealed class MacOverlayEffects : IOverlayEffects
{
    // NSWindowCollectionBehavior: CanJoinAllSpaces(1<<0) | Stationary(1<<4) | FullScreenAuxiliary(1<<8)
    // | IgnoresCycle(1<<6) — show on all Spaces and over other apps' fullscreen, don't move in Mission
    // Control, stay out of Cmd-` cycling.
    private const ulong CollectionBehavior = 0x1 | 0x10 | 0x100 | 0x40;
    private const long NSNormalWindowLevel = 0;

    private IntPtr _win;
    private long _topLevel;
    private DispatcherTimer? _timer;
    private bool _suppress;
    private bool _hidden;

    public void ConfigureOverlay(nint nativeHandle)
    {
        _win = MacNative.ToNSWindow((IntPtr)nativeHandle);
        if (_win == IntPtr.Zero) return;

        _topLevel = MacNative.CGWindowLevelForKey(MacNative.kCGScreenSaverWindowLevelKey);
        ApplyTopmost();
        MacNative.SendBool(_win, "setIgnoresMouseEvents:", true); // click-through by default

        // Re-assert level + collection behaviour periodically (Avalonia can reset the level on its own
        // window updates). orderFrontRegardless keeps us at the front of our level band without focus.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            if (_suppress || _hidden || _win == IntPtr.Zero) return;
            ApplyTopmost();
            MacNative.Send(_win, "orderFrontRegardless");
        };
        _timer.Start();
    }

    private void ApplyTopmost()
    {
        MacNative.SendULong(_win, "setCollectionBehavior:", CollectionBehavior);
        MacNative.SendLong(_win, "setLevel:", _topLevel);
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (_win != IntPtr.Zero) MacNative.SendBool(_win, "setIgnoresMouseEvents:", clickThrough);
    }

    public void Hide()
    {
        _hidden = true;
        if (_win != IntPtr.Zero) MacNative.SendArgPtr(_win, "orderOut:", IntPtr.Zero);
    }

    public void ShowNoActivate()
    {
        _hidden = false;
        if (_win == IntPtr.Zero) return;
        ApplyTopmost();
        MacNative.Send(_win, "orderFrontRegardless"); // show + raise without becoming key/activating
    }

    public bool SuppressTopmost
    {
        get => _suppress;
        set
        {
            _suppress = value;
            if (_win == IntPtr.Zero) return;
            // Drop to the normal window level while a focusable child (the say bar) is up, so it isn't
            // buried under the overlay; restore the top level when it closes. (NOT a no-op on macOS.)
            if (value) MacNative.SendLong(_win, "setLevel:", NSNormalWindowLevel);
            else if (!_hidden) ApplyTopmost();
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
