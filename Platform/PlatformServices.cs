using Avalonia.Controls;
using Avalonia.Media;
using MaplePet.Platform.Abstractions;
#if WINDOWS
using MaplePet.Platform.Windows;
#else
using MaplePet.Platform.Mac;
#endif

namespace MaplePet.Platform;

/// <summary>
/// Resolves the concrete platform-service bundle for the running OS. This is the ONLY place that names
/// the per-OS implementation types: the <c>WINDOWS</c>/<c>MACOS</c> compile constant (set per target
/// framework) picks the namespace, and the per-TFM <c>Compile Remove</c> in the csproj means each head
/// only ever sees its own platform folder. So the shared code (PetWindow, App, Program, SettingsView)
/// references only the abstractions and this factory — no <c>OperatingSystem.IsWindows()</c> branches.
/// </summary>
public static class PlatformServices
{
    /// <summary>Window-bound services (window tracker, overlay effects, input, hotkey), resolved once
    /// the overlay window's native handle exists (PetWindow.OnOpened).</summary>
    public static IPlatformServices Create(Window overlay) =>
#if WINDOWS
        new WindowsPlatformServices(overlay);
#else
        new MacPlatformServices(overlay);
#endif

    /// <summary>Force <paramref name="hwnd"/> to the foreground with keyboard focus, bypassing the OS
    /// foreground lock — used to focus the say/chat bar when it's summoned by the global hotkey while
    /// another app is active. No-op on macOS (and when the handle is 0).</summary>
    public static void ForceForeground(nint hwnd)
    {
#if WINDOWS
        WindowsInterop.ForceForeground(hwnd);
#endif
    }

    /// <summary>The single-instance guard acquired by Program.Main, exposed so the app can subscribe to
    /// <see cref="ISingleInstance.Activated"/>. Null until <see cref="AcquireSingleInstance"/> runs (and
    /// on non-interactive launches that skip it).</summary>
    public static ISingleInstance? SingleInstance { get; private set; }

    /// <summary>Acquire the single-instance guard (Program.Main). Never throws.</summary>
    public static ISingleInstance AcquireSingleInstance()
    {
        var instance =
#if WINDOWS
            WindowsSingleInstance.Acquire();
#else
            MacSingleInstance.Acquire();
#endif
        SingleInstance = instance;
        return instance;
    }

    /// <summary>Launch-at-login control (Settings toggle).</summary>
    public static IStartupAtLogin StartupAtLogin { get; } =
#if WINDOWS
        new WindowsStartupAtLogin();
#else
        new MacStartupAtLogin();
#endif

    /// <summary>Writable on-disk locations for settings / characters / the single-instance lock.</summary>
    public static IAppPaths AppPaths { get; } =
#if WINDOWS
        new WindowsAppPaths();
#else
        new MacAppPaths();
#endif

    /// <summary>Encrypted-at-rest store for chatbot secrets (provider API keys, the web-search key).
    /// Windows uses DPAPI under <see cref="IAppPaths.DataRoot"/>; macOS uses the login Keychain.</summary>
    public static ISecretStore SecretStore { get; } =
#if WINDOWS
        new WindowsSecretStore(AppPaths.DataRoot);
#else
        new MacSecretStore();
#endif

    // The dark Fluent menu surface the Windows tray popup paints behind each item (measured #2B2B2B).
    // The app forces a Dark theme variant, so this is stable. Glyphs are drawn onto this exact colour
    // so the icon tile is invisible against the menu (see TrayGlyphs). Unused on macOS (transparent).
    private static readonly Color TrayMenuSurface = Color.FromRgb(0x2B, 0x2B, 0x2B);

    /// <summary>Tray menu glyph renderer (built before the overlay window, so it's window-free).
    /// Windows draws on the opaque menu surface to dodge the per-pixel-alpha black box; macOS draws on a
    /// transparent background (NSMenu composites alpha correctly).</summary>
    public static ITrayGlyphs TrayGlyphs { get; } =
#if WINDOWS
        new TrayGlyphs(opaqueBackground: true, TrayMenuSurface);
#else
        new TrayGlyphs(opaqueBackground: false, default);
#endif
}
