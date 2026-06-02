using System;
using System.Globalization;
using Avalonia;

namespace MaplePet;

/// <summary>Application-wide flags set from the command line.</summary>
public static class AppState
{
    /// <summary>If &gt; 0, the overlay auto-closes after this many seconds (smoke test).</summary>
    public static double SmokeSeconds { get; set; }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppState.SmokeSeconds = ParseSmoke(args);
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
}
