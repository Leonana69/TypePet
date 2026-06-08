using System;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Mac;

/// <summary>
/// macOS "start at login" via <c>SMAppService.mainApp</c> (macOS 13+). Registers the running app bundle
/// as a login item. Requires a real .app bundle (SMAppService keys off the bundle id), so it reports
/// unsupported — and the Settings row is disabled — when run unbundled (e.g. <c>dotnet run</c>).
/// </summary>
public sealed class MacStartupAtLogin : IStartupAtLogin
{
    private const string ServiceManagement =
        "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";
    private const long StatusEnabled = 1; // SMAppServiceStatusEnabled

    private static readonly IntPtr SmClass = ResolveClass();

    public bool IsSupported => SmClass != IntPtr.Zero && IsBundled();

    public bool IsEnabled()
    {
        if (!IsSupported) return false;
        var svc = MacNative.SendRetPtr(SmClass, "mainAppService");
        return svc != IntPtr.Zero && MacNative.SendRetLong(svc, "status") == StatusEnabled;
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported) return false;
        var svc = MacNative.SendRetPtr(SmClass, "mainAppService");
        if (svc == IntPtr.Zero) return false;
        return MacNative.SendRetBoolPtr(svc,
            enabled ? "registerAndReturnError:" : "unregisterAndReturnError:", IntPtr.Zero);
    }

    private static IntPtr ResolveClass()
    {
        MacNative.LoadFramework(ServiceManagement); // ensure the class is loaded before resolving it
        return MacNative.objc_getClass("SMAppService");
    }

    private static bool IsBundled()
    {
        var p = Environment.ProcessPath;
        return p is not null && p.Contains(".app/Contents/MacOS/", StringComparison.Ordinal);
    }
}
