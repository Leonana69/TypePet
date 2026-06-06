#if MAPLEPET_SCRIPTING
using System;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using MaplePet.Api;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

/// <summary>
/// The sandboxed implementation of <see cref="CommandScriptHost"/> for <c>kind:script</c> commands, using
/// the managed Jint JavaScript engine. The sandbox is capability-confined: the script gets only a small
/// host API (say / clipboard / image / link / hold + pet expression/action/walk/face) and the
/// <c>args</c> string — there is NO filesystem, network, CLR, or process access. Hard resource limits
/// (statement count, memory, recursion, wall-clock timeout, cancellation) stop a runaway or malicious
/// upload from freezing the pet. Runs off the UI thread; pet calls marshal back internally.
/// </summary>
public static partial class CommandScriptHost
{
    private const int MaxStatements = 20_000;
    private const int MaxRecursion = 64;
    private const long MemoryLimitBytes = 4L * 1024 * 1024; // 4 MB
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(2);

    public static partial Task<CommandResult> RunAsync(CommandManifest m, string args, IPetControl? pet, CancellationToken ct)
        => Task.Run(() => Execute(m, args ?? "", pet, ct), ct);

    private static CommandResult Execute(CommandManifest m, string args, IPetControl? pet, CancellationToken ct)
    {
        string? text = null, clip = null, image = null, linkTitle = null, linkUrl = null;
        double? hold = m.HoldSeconds;

        try
        {
            var engine = new Jint.Engine(o =>
            {
                o.LimitRecursion(MaxRecursion);
                o.MaxStatements(MaxStatements);
                o.LimitMemory(MemoryLimitBytes);
                o.TimeoutInterval(WallClock);
                o.CancellationToken(ct);
                // No AllowClr() → no .NET type access; no fetch/require/IO globals are exposed.
            });

            engine.SetValue("args", args);
            engine.SetValue("say", new Action<string>(s => text = s));
            engine.SetValue("clipboard", new Action<string>(s => clip = s));
            engine.SetValue("hold", new Action<double>(s => { if (double.IsFinite(s) && s > 0) hold = s; }));
            engine.SetValue("image", new Action<string>(s => { if (IsSafeImage(s)) image = s; }));
            engine.SetValue("link", new Action<string, string>((t, u) => { linkTitle = t; linkUrl = u; }));
            engine.SetValue("expression", new Action<string, double>((n, s) =>
                Fire(() => pet?.Expression(n, double.IsFinite(s) && s > 0 ? s : (double?)null))));
            engine.SetValue("action", new Action<string, string>((n, mode) =>
                Fire(() => pet?.DoAction(n, string.IsNullOrWhiteSpace(mode) ? null : mode))));
            engine.SetValue("walk", new Action<double>(x => { if (double.IsFinite(x)) Fire(() => pet?.WalkTo(x)); }));
            engine.SetValue("face", new Action<string>(d => Fire(() => pet?.Face(d))));

            JsValue completion = engine.Evaluate(m.Body);

            // A returned primitive becomes the spoken text when the script didn't call say(…).
            if (string.IsNullOrEmpty(text) && completion is not null &&
                (completion.IsString() || completion.IsNumber() || completion.IsBoolean()))
                text = completion.ToString();
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"/{m.Name} script error: {Shorten(ex.Message)}");
        }

        // An optional reaction expression applies after the script (like the declarative kinds).
        if (!string.IsNullOrEmpty(m.ReactionExpression))
            Fire(() => pet?.Expression(m.ReactionExpression!, m.ReactionSeconds ?? hold));

        WebSource? link = string.IsNullOrWhiteSpace(linkUrl) ? null
            : new WebSource(string.IsNullOrWhiteSpace(linkTitle) ? linkUrl! : linkTitle!, linkUrl!, "");

        return CommandResult.Ok(
            string.IsNullOrEmpty(text) ? (m.Help ?? "") : text!,
            sources: link is null ? null : new[] { link },
            link: link,
            imageUrl: image,
            clipboardText: clip,
            holdSeconds: hold);
    }

    // A script can only point an image at a bundled or remote URL — never a local path it could probe.
    private static bool IsSafeImage(string? s) =>
        s is not null && (s.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                          || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static void Fire(Func<Task?> act) { try { _ = act(); } catch { /* pet may lack the move; ignore */ } }

    private static string Shorten(string s) => s.Length <= 160 ? s : s[..160] + "…";
}
#endif
