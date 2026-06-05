using System;
using Avalonia.Threading;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Windows;

/// <summary>
/// Windows overlay effects: makes the window click-through (WS_EX_TRANSPARENT|LAYERED|NOACTIVATE) and
/// keeps it pinned to the top of the topmost z-order. Avalonia sets WS_EX_TOPMOST once, but activating
/// another topmost window (e.g. a borderless-fullscreen game) raises it above us within the topmost band
/// and the overlay never takes focus to recover — so a ~500ms timer re-asserts the position. The timer
/// is suppressed while a focusable child (the say bar) is up, and while the overlay is hidden behind a
/// fullscreen app.
/// </summary>
public sealed class WindowsOverlayEffects : IOverlayEffects
{
    private nint _hwnd;
    private DispatcherTimer? _topmostTimer;
    private bool _suppressTopmost;
    private bool _hidden;

    public void ConfigureOverlay(nint nativeHandle)
    {
        _hwnd = nativeHandle;
        WindowsInterop.MakeClickThrough(_hwnd);

        // Re-assert topmost a couple of times a second; it's invisible (no move/size/activate) and a
        // no-op under true exclusive fullscreen, where DWM isn't compositing the desktop anyway.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _topmostTimer.Tick += (_, _) => { if (!_suppressTopmost && !_hidden) WindowsInterop.RaiseToTop(_hwnd); };
        _topmostTimer.Start();
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (_hwnd != 0) WindowsInterop.SetClickThrough(_hwnd, clickThrough);
    }

    public void Hide()
    {
        _hidden = true;
        if (_hwnd != 0) WindowsInterop.Hide(_hwnd);
    }

    public void ShowNoActivate()
    {
        _hidden = false;
        if (_hwnd == 0) return;
        WindowsInterop.ShowNoActivate(_hwnd);
        WindowsInterop.RaiseToTop(_hwnd);
    }

    public bool SuppressTopmost
    {
        get => _suppressTopmost;
        set => _suppressTopmost = value;
    }

    public void Dispose()
    {
        _topmostTimer?.Stop();
        _topmostTimer = null;
    }
}
