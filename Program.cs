using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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
        // Last-resort crash logging: a WinExe hides unhandled exceptions (the window just vanishes), so
        // write them to <DataRoot>/crash.log for diagnosis. See the Windows-run troubleshooting notes.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("UnobservedTask", e.Exception); e.SetObserved(); };

        AppState.SmokeSeconds = ParseSmoke(args);
        AppState.RenderPosesDir = ParseOption(args, "--render-poses");
        AppState.RenderPosesFrom = ParseOption(args, "--render-from");

        // Dev-only: run the pure-engine path-planner checks and exit (no Avalonia needed).
        if (ParseOption(args, "--nav-test") is string navOut)
        {
            MaplePet.Engine.NavTest.Run(navOut);
            return 0;
        }

        // Dev-only: smoke-test the MapleStory knowledge base (RAG). Needs Avalonia's asset loader for the
        // bundled catalog, so set up without starting the UI. Optional query: --rag-test "<question>".
        if (Array.IndexOf(args, "--rag-test") >= 0)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            return MaplePet.Api.Chat.RagTest.Run(ParseOption(args, "--rag-test"));
        }

        // Dev-only: smoke-test chat inline-markup rendering (bold/italic + clickable links). Builds real
        // controls, so set up Avalonia without starting the UI.
        if (Array.IndexOf(args, "--markup-test") >= 0)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            return MaplePet.Views.MarkupTest.Run();
        }

        // Dev-only: validate the user command library + merged registry headlessly, and optionally run one
        // command. Needs Avalonia's asset loader (bundled avares image refs / seeding). Usage:
        // --commands-test ["<name>"].
        if (Array.IndexOf(args, "--commands-test") >= 0)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            return MaplePet.Api.Chat.CommandTest.Run(ParseOption(args, "--commands-test"));
        }

        // Dev-only: print exactly what web_fetch / maple_lookup would extract from a page (incl. annotated
        // link targets). Pure HTTP + HTML parse, no Avalonia. Usage: --fetch-test "<url>".
        if (ParseOption(args, "--fetch-test") is string fetchUrl)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }
            Console.WriteLine(MaplePet.Api.Chat.WebTools.FetchReadableAsync(fetchUrl, System.Threading.CancellationToken.None).GetAwaiter().GetResult());
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

    /// <summary>Append an unhandled exception to <c>&lt;DataRoot&gt;/crash.log</c> (best effort).</summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(MaplePet.Platform.PlatformServices.AppPaths.DataRoot, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:u}] {source}: {ex}\n\n");
        }
        catch { /* nothing more we can do */ }
    }

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
