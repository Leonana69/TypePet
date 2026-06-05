using System;
using System.Collections.Generic;
using MaplePet.Engine;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>
/// macOS window tracker. Enumerates on-screen windows with CGWindowListCopyWindowInfo — which returns
/// bounds / layer / owner / PID WITHOUT any TCC permission (only the window <i>title</i> needs Screen
/// Recording, and we never read it). Bounds come back top-left origin, in points, in the global display
/// space — the same unit Avalonia reports for <c>Screens.Bounds</c> with <c>Scaling == 1</c> on macOS —
/// so the tracker returns them verbatim and the overlay's existing ScreenSpace (origin, scale 1) maps
/// them with no flip and no backing-scale multiply. The pet's home is the bottom of the screen (the Dock
/// top when on-screen, else the display's bottom edge); the menu bar is an extra walkable strip.
/// </summary>
public sealed class MacWindowTracker : IWindowTracker
{
    private const double MinWindowSize = 40;
    private const double MenuBarHeight = 24;   // approximate; the pet walks on the menu bar's bottom edge
    private const double GroundThickness = 4;

    private static readonly IntPtr KeyBounds = MacNative.CgConstant("kCGWindowBounds");
    private static readonly IntPtr KeyLayer = MacNative.CgConstant("kCGWindowLayer");
    private static readonly IntPtr KeyOwnerPid = MacNative.CgConstant("kCGWindowOwnerPID");
    private static readonly IntPtr KeyOwnerName = MacNative.CgConstant("kCGWindowOwnerName");
    private static readonly IntPtr KeyWindowNumber = MacNative.CgConstant("kCGWindowNumber");

    /// <summary>Our own full-screen overlay's window number (NSWindow.windowNumber == CGWindowList's
    /// kCGWindowNumber), excluded from the captured world — the macOS analogue of the Windows tracker's
    /// <c>ExcludeHwnd</c>. The overlay spans the whole screen and would otherwise read as a giant
    /// platform; every other MaplePet window (the config/character window, the say bar) is an ordinary
    /// window and stays walkable. 0 disables the match — in the normal case the overlay is still kept out
    /// by the window-level filter below, since it floats at screen-saver level.</summary>
    public long ExcludeWindowNumber { get; set; }

    public WorldGeometry Capture()
    {
        var windows = new List<Rect>();
        Rect? dock = null;

        IntPtr array = MacNative.CGWindowListCopyWindowInfo(
            MacNative.kCGWindowListOptionOnScreenOnly | MacNative.kCGWindowListExcludeDesktopElements,
            MacNative.kCGNullWindowID);
        if (array != IntPtr.Zero)
        {
            try
            {
                long count = MacNative.CFArrayGetCount(array);
                for (long i = 0; i < count; i++) // CGWindowList is front-to-back, the order WorldModel wants
                {
                    IntPtr dict = MacNative.CFArrayGetValueAtIndex(array, i);
                    if (dict == IntPtr.Zero) continue;

                    // Skip only our own full-screen overlay, matched by window number (like the Windows
                    // tracker's ExcludeHwnd). Other MaplePet windows — the config/character window and the
                    // say bar — are ordinary windows and stay walkable, so the pet can perch on them too.
                    if (ExcludeWindowNumber != 0 && ReadNumber(dict, KeyWindowNumber) == ExcludeWindowNumber) continue;
                    string? owner = ReadString(dict, KeyOwnerName);

                    if (owner == "Dock")
                    {
                        // Remember the Dock's tile (the largest Dock window) for the ground.
                        if (TryReadBounds(dict, out var db) &&
                            (dock is null || Area(db) > Area(dock.Value)))
                            dock = db;
                        continue;
                    }
                    if (IsShellOwner(owner)) continue;
                    if (ReadNumber(dict, KeyLayer) != 0) continue; // only normal app windows are walkable

                    if (!TryReadBounds(dict, out var r)) continue;
                    if (r.Width < MinWindowSize || r.Height < MinWindowSize) continue;
                    windows.Add(r);
                }
            }
            finally { MacNative.CFRelease(array); }
        }

        var display = MainDisplayBounds();

        // The menu bar (top strip) is an extra walkable surface.
        windows.Add(new Rect(display.X, display.Y, display.Width, MenuBarHeight));

        // Ground (the "taskbar" slot, edge Bottom so the pet walks on its top): the Dock's top when it
        // is on-screen, otherwise a thin strip at the bottom of the display.
        Rect ground = dock is Rect d && d.Y >= display.Y && d.Bottom <= display.Bottom + 1 && d.Width >= MinWindowSize
            ? d
            : new Rect(display.X, display.Bottom - GroundThickness, display.Width, GroundThickness);

        return new WorldGeometry(windows, ground, TaskbarEdge.Bottom);
    }

    /// <summary>
    /// True when the frontmost real app window covers an entire display's full frame (menu bar
    /// included) — a borderless / native-fullscreen app, not a merely zoomed window. Permission-free
    /// (CGWindowList + display bounds). Compared against the main display only (a v1 limitation for
    /// fullscreen apps on a secondary display).
    /// </summary>
    public bool IsForegroundFullscreen()
    {
        IntPtr array = MacNative.CGWindowListCopyWindowInfo(
            MacNative.kCGWindowListOptionOnScreenOnly | MacNative.kCGWindowListExcludeDesktopElements,
            MacNative.kCGNullWindowID);
        if (array == IntPtr.Zero) return false;

        try
        {
            long count = MacNative.CFArrayGetCount(array);
            int myPid = Environment.ProcessId;
            for (long i = 0; i < count; i++) // first eligible entry = frontmost
            {
                IntPtr dict = MacNative.CFArrayGetValueAtIndex(array, i);
                if (dict == IntPtr.Zero) continue;
                if ((int)ReadNumber(dict, KeyOwnerPid) == myPid) continue;

                string? owner = ReadString(dict, KeyOwnerName);
                if (owner == "Dock" || IsShellOwner(owner)) continue;
                if (ReadNumber(dict, KeyLayer) != 0) continue;
                if (!TryReadBounds(dict, out var r)) continue;
                if (r.Width < MinWindowSize || r.Height < MinWindowSize) continue;

                var disp = MainDisplayBounds();
                const double tol = 2;
                return r.Left <= disp.Left + tol && r.Top <= disp.Top + tol
                    && r.Right >= disp.Right - tol && r.Bottom >= disp.Bottom - tol;
            }
        }
        finally { MacNative.CFRelease(array); }
        return false;
    }

    private static Rect MainDisplayBounds()
    {
        var b = MacNative.CGDisplayBounds(MacNative.CGMainDisplayID());
        return new Rect(b.X, b.Y, b.W, b.H);
    }

    private static double Area(Rect r) => r.Width * r.Height;

    private static bool IsShellOwner(string? owner) => owner is null or
        "Window Server" or "WindowManager" or "Spotlight" or "Control Center" or
        "NotificationCenter" or "Wallpaper" or "Dock";

    private static long ReadNumber(IntPtr dict, IntPtr key)
    {
        if (key == IntPtr.Zero || !MacNative.CFDictionaryGetValueIfPresent(dict, key, out var num) || num == IntPtr.Zero)
            return 0;
        return MacNative.CFNumberGetValue(num, MacNative.kCFNumberSInt64Type, out long v) ? v : 0;
    }

    private static string? ReadString(IntPtr dict, IntPtr key)
    {
        if (key == IntPtr.Zero || !MacNative.CFDictionaryGetValueIfPresent(dict, key, out var str) || str == IntPtr.Zero)
            return null;
        return MacNative.CFStringToString(str);
    }

    private static bool TryReadBounds(IntPtr dict, out Rect rect)
    {
        rect = default;
        if (KeyBounds == IntPtr.Zero ||
            !MacNative.CFDictionaryGetValueIfPresent(dict, KeyBounds, out var boundsDict) ||
            boundsDict == IntPtr.Zero ||
            !MacNative.CGRectMakeWithDictionaryRepresentation(boundsDict, out var cg))
            return false;
        rect = new Rect(cg.X, cg.Y, cg.W, cg.H);
        return true;
    }
}
