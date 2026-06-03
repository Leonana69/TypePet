using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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

    private PetMcpServer(WebApplication app) => _app = app;

    /// <summary>Build and start the server (non-blocking) bound to localhost <paramref name="port"/>.</summary>
    public static PetMcpServer Start(IPetControl control, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // this runs inside a GUI app — keep the console quiet

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
    public void Stop()
    {
        try { _app.StopAsync().GetAwaiter().GetResult(); }
        catch { /* best effort */ }
    }
}
