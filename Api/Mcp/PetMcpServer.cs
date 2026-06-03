using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MaplePet.Api.Mcp;

/// <summary>
/// Hosts the pet's <see cref="IPetControl"/> as a local MCP tool server over Streamable HTTP on
/// <c>127.0.0.1:&lt;port&gt;</c>, so an MCP-capable LLM/agent can discover (<c>tools/list</c>) and
/// drive the pet. The pet is a long-running GUI app the agent connects to, so HTTP fits better than
/// stdio (which assumes the client spawns the process). Off by default — enabled via
/// <c>Settings.EnableMcpServer</c>.
///
/// The tool set is static (see <see cref="PetTools"/>); the per-character vocabulary is delivered by
/// the <c>capabilities</c> tool, which the agent re-calls after a character swap — so no
/// <c>tools/list_changed</c> notification is needed.
/// </summary>
public sealed class PetMcpServer
{
    private readonly WebApplication _app;
    private bool _stopped; // guard: Stop() may be reached more than once (e.g. Exit raised twice)

    private PetMcpServer(WebApplication app) => _app = app;

    /// <summary>Build and start the server (non-blocking) bound to localhost <paramref name="port"/>.</summary>
    public static PetMcpServer Start(IPetControl control, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // this runs inside a GUI app — keep the console quiet

        // Bound the host's graceful-shutdown drain so a slow/stuck hosted service (e.g. an open
        // Streamable-HTTP/SSE stream) can't stall StopAsync longer than this — defense in depth
        // alongside the cancellation token Stop() passes to StopAsync.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

        builder.Services.AddSingleton(control); // injected into the PetTools methods
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<PetTools>();

        var app = builder.Build();
        app.MapMcp();

        var server = new PetMcpServer(app);
        _ = app.RunAsync($"http://127.0.0.1:{port}"); // runs on the thread pool; doesn't block the UI
        return server;
    }

    /// <summary>Stop the server (best effort, on app shutdown).</summary>
    /// <remarks>
    /// Called from the Avalonia <c>desktop.Exit</c> handler, which runs synchronously on the UI thread
    /// while the dispatcher loop is alive but NOT pumping. The host was started by <see cref="Start"/>'s
    /// fire-and-forget <c>app.RunAsync</c> from the UI thread, so some of its (and its background
    /// services') continuations are captured against the Avalonia SynchronizationContext. Blocking the
    /// UI thread on <c>StopAsync().GetAwaiter().GetResult()</c> therefore deadlocks: those continuations
    /// can never run, so the host never finishes stopping. We instead run the whole stop+dispose on a
    /// plain thread-pool thread (Task.Run clears the captured sync-context) and wait with a bounded
    /// timeout, so Exit always returns and the process can terminate even if a client holds a stream
    /// open.
    /// </remarks>
    public void Stop()
    {
        if (_stopped) return; // idempotent: a second Stop() would hit an already-disposed host
        _stopped = true;
        try
        {
            // Task.Run hops to a thread-pool thread with NO SynchronizationContext, so StopAsync's
            // continuations resume there instead of being posted to the (blocked) UI dispatcher.
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(cts.Token).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(6));
        }
        catch { /* best effort: never let shutdown hang on the server */ }
    }
}
