using System.Threading;
using System.Threading.Tasks;
using MaplePet.Api;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

/// <summary>
/// Runs a <see cref="CommandKind.Script"/> command's body in a sandboxed scripting engine. The sandbox
/// grants NO filesystem, network, or CLR access — only a small host API that maps back to a
/// <see cref="CommandResult"/> / <see cref="IPetControl"/> — and enforces hard resource limits so a
/// runaway script can't freeze the pet. Gating (the user's scripts-enabled setting) is handled by the
/// caller; this host assumes it's allowed to run.
/// </summary>
public static partial class CommandScriptHost
{
    /// <summary>Execute <paramref name="m"/>'s script body. Never throws — failures (including a disabled
    /// or absent engine) come back as an error <see cref="CommandResult"/>. <paramref name="networkApproved"/>
    /// is true only when the user has granted this command's declared <c>hosts:</c> allowlist; the
    /// <c>httpGet</c>/<c>httpJson</c> host functions are wired in only then.</summary>
    public static partial Task<CommandResult> RunAsync(CommandManifest m, string args, IPetControl? pet,
        bool networkApproved, CancellationToken ct);
}

#if !MAPLEPET_SCRIPTING
/// <summary>Fallback when the scripting engine isn't compiled in: report a friendly error instead of
/// running. The real sandboxed implementation (the other half of this partial) is compiled when the
/// Jint package is referenced and the <c>MAPLEPET_SCRIPTING</c> constant is defined.</summary>
public static partial class CommandScriptHost
{
    public static partial Task<CommandResult> RunAsync(CommandManifest m, string args, IPetControl? pet,
        bool networkApproved, CancellationToken ct)
        => Task.FromResult(CommandResult.Error($"/{m.Name}: scripting isn't available in this build."));
}
#endif
