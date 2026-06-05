using System;
using System.Globalization;
using Avalonia;

namespace MaplePet;

/// <summary>Application-wide flags set from the command line.</summary>
public static class AppState
{
    /// <summary>If &gt; 0, the overlay auto-closes after this many seconds (smoke test).</summary>
    public static double SmokeSeconds { get; set; }

    /// <summary>Dev-only: if set, render the character poses to PNGs in this dir and exit.</summary>
    public static string? RenderPosesDir { get; set; }

    /// <summary>Dev-only: footage dir for <see cref="RenderPosesDir"/> (defaults to Assets/footage).</summary>
    public static string? RenderPosesFrom { get; set; }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppState.SmokeSeconds = ParseSmoke(args);
        AppState.RenderPosesDir = ParseOption(args, "--render-poses");
        AppState.RenderPosesFrom = ParseOption(args, "--render-from");

        // Dev-only: run the pure-engine path-planner checks and exit (no Avalonia needed).
        if (ParseOption(args, "--nav-test") is string navOut)
        {
            MaplePet.Engine.NavTest.Run(navOut);
            return 0;
        }

#if MACOS
        // Dev-only: dump the captured macOS world (CGWindowList geometry) and exit.
        if (Array.IndexOf(args, "--mac-windump") >= 0)
        {
            MaplePet.Platform.Mac.MacDiagnostics.DumpWorld();
            return 0;
        }
        if (Array.IndexOf(args, "--mac-windows-all") >= 0)
        {
            MaplePet.Platform.Mac.MacDiagnostics.DumpAllWindows();
            return 0;
        }
#endif

        // Enforce a single running pet for the normal interactive launch. Dev/test invocations
        // (--render-poses, --smoke) are exempt: they're short-lived and shouldn't be blocked by — or
        // register as — the live instance.
        bool interactiveRun = AppState.RenderPosesDir is null && AppState.SmokeSeconds <= 0;
        MaplePet.Platform.Abstractions.ISingleInstance? instance = null;
        if (interactiveRun)
        {
            instance = MaplePet.Platform.PlatformServices.AcquireSingleInstance();
            if (!instance.IsOwner)
            {
                instance.SignalOwner(); // poke the already-running pet to acknowledge, then bow out
                instance.Dispose();
                return 0;
            }
        }

        // Owner: hold the mutex for the whole run (using over a null instance is a no-op in dev/test).
        using (instance)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static double ParseSmoke(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--smoke") continue;
            if (i + 1 < args.Length &&
                double.TryParse(args[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return s;
            return 3;
        }
        return 0;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
