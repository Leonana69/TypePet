using System;
using System.Runtime.InteropServices;

namespace MaplePet.Platform.Mac;

/// <summary>
/// Raw P/Invoke bindings to the macOS system frameworks (Objective-C runtime, CoreGraphics,
/// CoreFoundation), plus a few thin helpers. Deliberately tiny and self-contained: the whole native
/// surface is one CoreGraphics window-list call, a handful of CF readers, the cursor position, and a
/// dozen <c>objc_msgSend</c> calls against the NSWindow Avalonia already owns — so plain interop is
/// cleaner than pulling in the heavyweight net*-macos AppKit binding workload. arm64 has a single
/// <c>objc_msgSend</c> for every signature (no _stret/_fpret split).
/// </summary>
internal static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    // ---------------- Objective-C runtime ----------------
    [DllImport(ObjC)] internal static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] internal static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr msgSend(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr msgSend(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void msgSendVoid(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void msgSendULong(IntPtr receiver, IntPtr sel, ulong a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void msgSendLong(IntPtr receiver, IntPtr sel, long a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void msgSendBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern long msgSendRetLong(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool msgSendRetBool(IntPtr receiver, IntPtr sel, IntPtr a);

    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>Normalize an Avalonia platform handle (which may be the NSWindow or its content NSView)
    /// to the owning NSWindow.</summary>
    internal static IntPtr ToNSWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var nsWindowClass = objc_getClass("NSWindow");
        bool isWindow = msgSendRetBool(handle, Sel("isKindOfClass:"), nsWindowClass);
        return isWindow ? handle : msgSend(handle, Sel("window"));
    }

    // Convenience senders keyed by selector name (selectors are cheap and cached by the runtime).
    internal static void Send(IntPtr recv, string sel) => msgSendVoid(recv, Sel(sel));
    internal static void SendULong(IntPtr recv, string sel, ulong a) => msgSendULong(recv, Sel(sel), a);
    internal static void SendLong(IntPtr recv, string sel, long a) => msgSendLong(recv, Sel(sel), a);
    internal static void SendBool(IntPtr recv, string sel, bool a) => msgSendBool(recv, Sel(sel), a);
    internal static long SendRetLong(IntPtr recv, string sel) => msgSendRetLong(recv, Sel(sel));
    internal static IntPtr SendRetPtr(IntPtr recv, string sel) => msgSend(recv, Sel(sel));
    internal static IntPtr SendArgPtr(IntPtr recv, string sel, IntPtr a) => msgSend(recv, Sel(sel), a);
    internal static bool SendRetBoolPtr(IntPtr recv, string sel, IntPtr a) => msgSendRetBool(recv, Sel(sel), a);

    // ---------------- CoreGraphics ----------------
    [StructLayout(LayoutKind.Sequential)] internal struct CGRect { public double X, Y, W, H; }
    [StructLayout(LayoutKind.Sequential)] internal struct CGPoint { public double X, Y; }

    internal const uint kCGWindowListOptionOnScreenOnly = 1;       // 1 << 0
    internal const uint kCGWindowListExcludeDesktopElements = 16;  // 1 << 4
    internal const uint kCGNullWindowID = 0;
    internal const int kCGScreenSaverWindowLevelKey = 13;

    [DllImport(CG)] internal static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow); // CFArrayRef (owned)
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);
    [DllImport(CG)] internal static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] internal static extern CGPoint CGEventGetLocation(IntPtr @event);
    [DllImport(CG)] internal static extern long CGWindowLevelForKey(int key);
    [DllImport(CG)] internal static extern uint CGMainDisplayID();
    [DllImport(CG)] internal static extern CGRect CGDisplayBounds(uint display); // global space, top-left, points

    // ---------------- CoreFoundation ----------------
    internal const long kCFNumberSInt64Type = 4;
    internal const long kCFNumberFloat64Type = 6;
    internal const uint kCFStringEncodingUTF8 = 0x08000100;

    [DllImport(CF)] internal static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CF)] internal static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long idx);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFDictionaryGetValueIfPresent(IntPtr dict, IntPtr key, out IntPtr value);
    [DllImport(CF)] internal static extern void CFRelease(IntPtr cf);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFNumberGetValue(IntPtr number, long theType, out long value);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFNumberGetValue(IntPtr number, long theType, out double value);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

    // ---------------- Carbon (global hotkey: RegisterEventHotKey is permission-free) ----------------
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)] internal struct EventTypeSpec { public uint EventClass; public uint EventKind; }
    [StructLayout(LayoutKind.Sequential)] internal struct EventHotKeyID { public uint Signature; public uint Id; }

    internal const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    internal const uint kEventHotKeyPressed = 5;

    /// <summary>Carbon EventHandlerProcPtr: returns an OSStatus (0 = noErr consumes the event).</summary>
    internal delegate int EventHandlerProc(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [DllImport(Carbon)] internal static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] internal static extern int InstallEventHandler(IntPtr target, IntPtr handler, int numTypes, EventTypeSpec[] typeList, IntPtr userData, out IntPtr outRef);
    [DllImport(Carbon)] internal static extern int RegisterEventHotKey(uint hotKeyCode, uint hotKeyModifiers, EventHotKeyID hotKeyID, IntPtr target, uint options, out IntPtr outRef);
    [DllImport(Carbon)] internal static extern int UnregisterEventHotKey(IntPtr hotKey);

    // ---------------- dlopen/dlsym (for CoreGraphics' exported CFString key constants) ----------------
    [DllImport(LibSystem)] private static extern IntPtr dlopen(string path, int mode);
    [DllImport(LibSystem)] private static extern IntPtr dlsym(IntPtr handle, string symbol);

    /// <summary>Load a framework so its Objective-C classes (e.g. SMAppService) resolve.</summary>
    internal static IntPtr LoadFramework(string path) => dlopen(path, 2 /* RTLD_NOW */);

    private static readonly IntPtr CgHandle = dlopen(CG, 2 /* RTLD_NOW */);

    /// <summary>Read an exported CFStringRef constant (e.g. kCGWindowBounds) from CoreGraphics. The
    /// symbol is a pointer to the CFStringRef, so dereference once.</summary>
    internal static IntPtr CgConstant(string symbol)
    {
        var addr = dlsym(CgHandle, symbol);
        return addr == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(addr);
    }

    /// <summary>Read a CFString value into a managed string (UTF-8). Returns null on failure/empty.</summary>
    internal static string? CFStringToString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;
        var buf = new byte[512];
        if (!CFStringGetCString(cfString, buf, buf.Length, kCFStringEncodingUTF8)) return null;
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return len == 0 ? null : System.Text.Encoding.UTF8.GetString(buf, 0, len);
    }
}
