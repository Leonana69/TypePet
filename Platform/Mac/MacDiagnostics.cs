using System;
using System.Globalization;

namespace MaplePet.Platform.Mac;

/// <summary>Dev-only: dumps the captured macOS world (CGWindowList → WorldGeometry) so coordinates can be
/// sanity-checked against the real desktop without launching the GUI. Invoked via <c>--mac-windump</c>.</summary>
public static class MacDiagnostics
{
    public static void DumpWorld()
    {
        var disp = MacNative.CGDisplayBounds(MacNative.CGMainDisplayID());
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Main display (points, top-left): x={0} y={1} w={2} h={3}", disp.X, disp.Y, disp.W, disp.H));

        var tracker = new MacWindowTracker();
        var world = tracker.Capture();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Ground (edge={0}): x={1:0.#} y={2:0.#} w={3:0.#} h={4:0.#}",
            world.TaskbarEdge, world.Taskbar.X, world.Taskbar.Y, world.Taskbar.Width, world.Taskbar.Height));
        Console.WriteLine($"Walkable windows: {world.Windows.Count}");
        foreach (var r in world.Windows)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  x={0,8:0.#} y={1,8:0.#} w={2,8:0.#} h={3,8:0.#}", r.X, r.Y, r.Width, r.Height));
        Console.WriteLine($"Foreground fullscreen: {tracker.IsForegroundFullscreen()}");
    }

    /// <summary>Dump EVERY on-screen window (all layers) with owner, layer, alpha and bounds — used to
    /// check whether a given app's window is actually presented (alpha &gt; 0, on-screen bounds).</summary>
    public static void DumpAllWindows()
    {
        IntPtr kBounds = MacNative.CgConstant("kCGWindowBounds");
        IntPtr kLayer = MacNative.CgConstant("kCGWindowLayer");
        IntPtr kOwner = MacNative.CgConstant("kCGWindowOwnerName");
        IntPtr kAlpha = MacNative.CgConstant("kCGWindowAlpha");

        IntPtr arr = MacNative.CGWindowListCopyWindowInfo(MacNative.kCGWindowListOptionOnScreenOnly, MacNative.kCGNullWindowID);
        if (arr == IntPtr.Zero) { Console.WriteLine("no on-screen windows"); return; }
        try
        {
            long n = MacNative.CFArrayGetCount(arr);
            for (long i = 0; i < n; i++)
            {
                IntPtr d = MacNative.CFArrayGetValueAtIndex(arr, i);
                if (d == IntPtr.Zero) continue;
                string owner = Str(d, kOwner) ?? "?";
                long layer = Num(d, kLayer);
                double alpha = Dbl(d, kAlpha);
                var b = Bounds(d, kBounds);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-24} layer={1,5} alpha={2:0.00} bounds=({3:0},{4:0} {5:0}x{6:0})",
                    owner, layer, alpha, b.X, b.Y, b.Width, b.Height));
            }
        }
        finally { MacNative.CFRelease(arr); }
    }

    private static long Num(IntPtr d, IntPtr key) =>
        key != IntPtr.Zero && MacNative.CFDictionaryGetValueIfPresent(d, key, out var v) && v != IntPtr.Zero
            && MacNative.CFNumberGetValue(v, MacNative.kCFNumberSInt64Type, out long n) ? n : 0;

    private static double Dbl(IntPtr d, IntPtr key) =>
        key != IntPtr.Zero && MacNative.CFDictionaryGetValueIfPresent(d, key, out var v) && v != IntPtr.Zero
            && MacNative.CFNumberGetValue(v, MacNative.kCFNumberFloat64Type, out double n) ? n : 0;

    private static string? Str(IntPtr d, IntPtr key) =>
        key != IntPtr.Zero && MacNative.CFDictionaryGetValueIfPresent(d, key, out var v) && v != IntPtr.Zero
            ? MacNative.CFStringToString(v) : null;

    private static MaplePet.Engine.Rect Bounds(IntPtr d, IntPtr key)
    {
        if (key != IntPtr.Zero && MacNative.CFDictionaryGetValueIfPresent(d, key, out var v) && v != IntPtr.Zero
            && MacNative.CGRectMakeWithDictionaryRepresentation(v, out var cg))
            return new MaplePet.Engine.Rect(cg.X, cg.Y, cg.W, cg.H);
        return default;
    }
}
