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

        var displays = ActiveDisplayBounds();
        var mainDisplay = MainDisplayBounds();

        // Locate the Dock and which display it's on (it can sit on any display, e.g. moved there or
        // auto-shown under the cursor). The Dock floors that display via the Taskbar slot below; every
        // OTHER display gets a thin bottom strip so it has a floor too — this is the fix for a pet on a
        // Dock-less secondary display falling through the bottom.
        bool dockVisible = false;
        Rect dockRect = default, dockDisplay = mainDisplay;
        if (dock is Rect dk && dk.Width >= MinWindowSize && dk.Height > 0)
        {
            double dcx = dk.X + dk.Width / 2, dcy = dk.Y + dk.Height / 2;
            foreach (var disp in displays)
                if (dcx >= disp.X && dcx <= disp.Right && dcy >= disp.Y && dcy <= disp.Bottom)
                {
                    if (dk.Bottom <= disp.Bottom + 1) { dockVisible = true; dockRect = dk; dockDisplay = disp; }
                    break;
                }
        }

        // The display the Taskbar slot covers (so we don't also add a strip for it).
        Rect floorDisplay = dockVisible ? dockDisplay : mainDisplay;

        // Menu bar (top strip) as a walkable surface — only on the MAIN display. macOS shows a menu bar
        // on every display ("separate Spaces"), but treating a secondary display's menu bar as a platform
        // is unwanted, so it's added for the main display alone.
        windows.Add(new Rect(mainDisplay.X, mainDisplay.Y, mainDisplay.Width, MenuBarHeight));

        var grounds = new List<Rect>();
        foreach (var disp in displays)
        {
            if (Same(disp, floorDisplay)) continue; // floored by the Taskbar slot
            grounds.Add(new Rect(disp.X, disp.Bottom - GroundThickness, disp.Width, GroundThickness));
        }

        // The canonical ground (Taskbar slot, edge Bottom so the pet walks on its top): the Dock when
        // on-screen, otherwise a thin strip at the bottom of the main display.
        Rect ground = dockVisible
            ? dockRect
            : new Rect(mainDisplay.X, mainDisplay.Bottom - GroundThickness, mainDisplay.Width, GroundThickness);

        return new WorldGeometry(windows, ground, TaskbarEdge.Bottom) { Grounds = grounds };
    }

    /// <summary>
    /// True when the frontmost real app window covers an entire display's full frame (menu bar
    /// included) — a borderless / native-fullscreen app, not a merely zoomed window. Permission-free
    /// (CGWindowList + display bounds). Compared against the display the window actually sits on, so a
    /// fullscreen app on any monitor hides the pet (which may be confined to that monitor).
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

                var disp = DisplayFor(r);
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

    /// <summary>All active displays' bounds (global space, top-left, points — the same unit as the
    /// captured window rects). Falls back to the main display if enumeration fails, so capture is never
    /// without a display to floor.</summary>
    private static List<Rect> ActiveDisplayBounds()
    {
        var result = new List<Rect>();
        if (MacNative.CGGetActiveDisplayList(0, null, out uint count) == 0 && count > 0)
        {
            var ids = new uint[count];
            if (MacNative.CGGetActiveDisplayList(count, ids, out uint got) == 0)
                for (uint i = 0; i < got; i++)
                {
                    var b = MacNative.CGDisplayBounds(ids[i]);
                    result.Add(new Rect(b.X, b.Y, b.W, b.H));
                }
        }
        if (result.Count == 0) result.Add(MainDisplayBounds());
        return result;
    }

    /// <summary>The active display a window sits on: the one containing its center, else the one it
    /// overlaps most, else the main display.</summary>
    private static Rect DisplayFor(Rect r)
    {
        var displays = ActiveDisplayBounds();
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        foreach (var d in displays)
            if (cx >= d.X && cx <= d.Right && cy >= d.Y && cy <= d.Bottom) return d;

        Rect best = displays[0];
        double bestOverlap = -1;
        foreach (var d in displays)
        {
            double ox = Math.Max(0, Math.Min(r.Right, d.Right) - Math.Max(r.Left, d.Left));
            double oy = Math.Max(0, Math.Min(r.Bottom, d.Bottom) - Math.Max(r.Top, d.Top));
            double area = ox * oy;
            if (area > bestOverlap) { bestOverlap = area; best = d; }
        }
        return best;
    }

    /// <summary>Same display rect within a small tolerance (the same display id yields identical
    /// CGDisplayBounds, but compare with epsilon to be safe).</summary>
    private static bool Same(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1
        && Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;

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
