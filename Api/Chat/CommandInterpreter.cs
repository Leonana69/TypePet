using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MaplePet.Api;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

/// <summary>
/// Turns a declarative <see cref="CommandManifest"/> into a runnable command — the same
/// <c>Func&lt;args, ct, Task&lt;CommandResult&gt;&gt;</c> shape the built-in commands use — so user
/// commands plug into <see cref="ChatCommands"/> without special-casing. Each <see cref="CommandKind"/>
/// maps onto the existing output surface (a <see cref="CommandResult"/> and/or <see cref="IPetControl"/>):
/// nothing here executes arbitrary code except <see cref="CommandKind.Script"/>, which runs in a
/// sandboxed engine (see <c>CommandScriptHost</c>) gated by the user's scripts-enabled setting.
///
/// Lives in Api/Chat (not Engine) because it bridges to chat types (<see cref="ChatRequest"/>,
/// <see cref="ChatSessionConfig"/>) and <see cref="IPetControl"/>; the manifest + store stay Engine-pure.
/// It's built with the same callbacks <see cref="ChatCommands"/> already holds, so wiring is unchanged.
/// </summary>
public sealed class CommandInterpreter
{
    private const int MaxPetSteps = 32;

    /// <summary>How long the pet wears a <c>reaction:</c> face when that reaction doesn't carry its own
    /// <c>|seconds</c> override. The bubble's <see cref="CommandManifest.HoldSeconds"/> is independent: a
    /// reaction is a brief "afterward" face, so it no longer inherits the (often much longer) bubble hold —
    /// and a command that sets a reaction but no hold no longer leaves the face stuck forever.</summary>
    private const double DefaultExpressionSeconds = 10;

    private readonly Func<bool> _chatEnabled;
    private readonly Func<ChatSessionConfig?> _buildConfig;
    private readonly Func<IPetControl?> _pet;
    private readonly Func<bool> _scriptsEnabled;
    private readonly Func<string, IReadOnlyCollection<string>, bool> _networkApproved;

    public CommandInterpreter(Func<bool> chatEnabled, Func<ChatSessionConfig?> buildConfig,
        Func<IPetControl?> pet, Func<bool> scriptsEnabled,
        Func<string, IReadOnlyCollection<string>, bool>? networkApproved = null)
    {
        _chatEnabled = chatEnabled;
        _buildConfig = buildConfig;
        _pet = pet;
        _scriptsEnabled = scriptsEnabled;
        _networkApproved = networkApproved ?? ((_, _) => false);
    }

    /// <summary>Build the handler for <paramref name="m"/> (its folder is <paramref name="dir"/>, used to
    /// resolve folder-relative assets). The returned delegate never throws — failures come back as an
    /// error <see cref="CommandResult"/> (and <see cref="ChatCommands.RunAsync"/> wraps it too).</summary>
    public Func<string, CancellationToken, Task<CommandResult>> Build(CommandManifest m, string dir) => m.Kind switch
    {
        CommandKind.Text => (args, _) => RunText(m, args),
        CommandKind.Clipboard => (args, _) => RunClipboard(m, args),
        CommandKind.Image => (args, _) => RunImage(m, dir, args),
        CommandKind.Link => (args, _) => RunLink(m, args),
        CommandKind.Prompt => (args, ct) => RunPrompt(m, args, ct),
        CommandKind.Pet => (args, ct) => RunPet(m, args, ct),
        CommandKind.Script => (args, ct) => RunScript(m, dir, args, ct),
        _ => (_, _) => Task.FromResult(CommandResult.Error($"/{m.Name}: unsupported command kind \"{m.RawKind}\".")),
    };

    // -------------------------------------------------------------------- kinds
    private async Task<CommandResult> RunText(CommandManifest m, string args)
    {
        string text = CommandManifest.Substitute(string.IsNullOrEmpty(m.Text) ? m.Body : m.Text, args);
        await ApplyReaction(m);
        return CommandResult.Ok(text, holdSeconds: m.HoldSeconds);
    }

    private Task<CommandResult> RunClipboard(CommandManifest m, string args)
    {
        string text = CommandManifest.Substitute(m.Clipboard ?? "", args);
        return Task.FromResult(CommandResult.Ok($"Copied \"{text}\" to clipboard 📋",
            clipboardText: text, holdSeconds: m.HoldSeconds));
    }

    private async Task<CommandResult> RunImage(CommandManifest m, string dir, string args)
    {
        string? uri = ResolveImage(m.Image ?? "", dir);
        if (uri is null) return CommandResult.Error($"/{m.Name}: image not found.");
        await ApplyReaction(m);
        string caption = string.IsNullOrWhiteSpace(m.Help) ? m.Name : m.Help!;
        return CommandResult.Ok(caption, imageUrl: uri, holdSeconds: m.HoldSeconds ?? 120);
    }

    private async Task<CommandResult> RunLink(CommandManifest m, string args)
    {
        string url = CommandManifest.Substitute(m.LinkUrl ?? "", args);
        if (string.IsNullOrWhiteSpace(url)) return CommandResult.Error($"/{m.Name}: missing link url.");
        var src = new WebSource(string.IsNullOrWhiteSpace(m.LinkTitle) ? url : m.LinkTitle!, url, "");
        await ApplyReaction(m);
        string text = string.IsNullOrWhiteSpace(m.Help) ? url : m.Help!;
        return CommandResult.Ok(text, sources: new[] { src }, link: src, holdSeconds: m.HoldSeconds);
    }

    private async Task<CommandResult> RunPrompt(CommandManifest m, string args, CancellationToken ct)
    {
        if (m.RequiresChat && !_chatEnabled())
            return CommandResult.Error($"/{m.Name} needs the chatbot — enable it in Settings → Chatbot.");
        var cfg = _buildConfig();
        if (cfg is null)
            return CommandResult.Error($"/{m.Name} needs a chat provider — add an API key in Settings → Chatbot.");

        string name = args.Trim();
        if (name.Length == 0) name = "Mapler";

        // The worn character's expressions (best-effort): exposed to the prompt as {{expressions}}, and used
        // to validate an expression the model picks for the pet to wear.
        var pet = _pet();
        IReadOnlyList<string> expressions = Array.Empty<string>();
        if (pet is not null)
        {
            try { expressions = (await pet.GetCapabilities()).Expressions.Select(e => e.Name).ToArray(); }
            catch { /* pet not ready — carry on without expressions */ }
        }

        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = name,
            // {{date}} is kept as a back-compat alias (date only), but the current date AND time are now
            // injected automatically below, so authors no longer need any placeholder for "now".
            ["date"] = DateTime.Now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture),
            ["expressions"] = expressions.Count > 0 ? string.Join(", ", expressions) : "(none)",
        };
        if (m.Roll.Count > 0) named["roll"] = m.PickRoll(); // system-rolled, weighted outcome (not the model's whim)

        // The body is the system prompt (persona/instructions); the typed arguments are the user turn. The
        // current date/time is prepended automatically so every persona knows "now" without a placeholder.
        string body = CommandManifest.Substitute(m.Body, args, named);
        // RAG: resolve any inline {{web_fetch(...)}} / {{web_search(...)}} directives into the prompt before
        // sending. Gated on the Web-search toggle (cfg.Web is non-null only when it's on), so it obeys the
        // same kill-switch as the web_fetch/web_search tools. Substituted first so a url may use {{1}}/{{args}}.
        if (cfg.Web is not null)
            body = await PromptRag.ExpandAsync(body, ct).ConfigureAwait(false);
        string system = $"Current date and time (the user's local time): {PromptTime.Now()}.\n\n" + body;
        string user = args.Length > 0 ? args : "Go.";
        var req = new ChatRequest(system, new[] { ChatMessage.User(user) },
            Array.Empty<ChatToolDef>(), cfg.Model, cfg.MaxTokens);

        ChatTurn turn = await cfg.Backend.SendAsync(req, ct).ConfigureAwait(false);

        // A prompt command may let the model drive the pet's face with a leading "Expression: <name>" line;
        // it's always stripped from the spoken text. A valid pick is worn, else the manifest's reaction.
        var (picked, text) = ExtractExpression(turn.Text ?? "", expressions);
        if (text.Length == 0) return CommandResult.Error($"/{m.Name}: the model returned nothing — try again.");

        if (pet is not null)
        {
            try
            {
                if (picked is not null) _ = pet.Expression(picked, m.HoldSeconds);
                else
                {
                    var (expr, secs) = m.PickReaction();
                    if (!string.IsNullOrEmpty(expr)) _ = pet.Expression(expr!, secs ?? DefaultExpressionSeconds);
                }
            }
            catch { /* character may lack the expression — ignore */ }
        }
        return CommandResult.Ok(text, holdSeconds: m.HoldSeconds);
    }

    /// <summary>Split a reply into (chosen expression, spoken text): find a leading "Expression: name" line,
    /// remove it regardless, and return the name only when the character actually has it (case-insensitive),
    /// so a stray directive never leaks into the bubble even if the name is unknown.</summary>
    private static (string? expression, string text) ExtractExpression(string raw, IReadOnlyCollection<string> available)
    {
        var kept = new List<string>();
        string? picked = null;
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var mm = Regex.Match(line, @"^\s*expression\s*[:=]\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (picked is null && mm.Success)
            {
                string val = mm.Groups[1].Value.Trim().Trim('[', ']', '"', '\'', '*', '.');
                picked = available.FirstOrDefault(n => string.Equals(n, val, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            kept.Add(line);
        }
        return (picked, string.Join("\n", kept).Trim());
    }

    private async Task<CommandResult> RunPet(CommandManifest m, string args, CancellationToken ct)
    {
        var pet = _pet();
        if (pet is null) return CommandResult.Error($"/{m.Name}: the pet isn't ready.");

        var says = new List<string>();
        int steps = 0;
        foreach (var raw in m.Body.Replace("\r\n", "\n").Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (++steps > MaxPetSteps) break;

            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            string op = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
            string rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
            string[] tok = rest.Length == 0 ? Array.Empty<string>()
                : rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            try
            {
                switch (op)
                {
                    case "say": says.Add(CommandManifest.Substitute(rest, args)); break;
                    case "expression": case "face_expr":
                        if (tok.Length > 0) await pet.Expression(tok[0], tok.Length > 1 && TryD(tok[1], out var es) ? es : null);
                        break;
                    case "do_action": case "action":
                        if (tok.Length > 0) await pet.DoAction(tok[0], tok.Length > 1 ? tok[1] : null);
                        break;
                    case "walk_to": case "walk":
                        if (tok.Length > 0 && TryD(tok[0], out var wx)) await pet.WalkTo(wx);
                        break;
                    case "move_to": case "move":
                        if (tok.Length > 1 && TryD(tok[0], out var mx) && TryD(tok[1], out var my)) await pet.MoveTo(mx, my);
                        break;
                    case "face":
                        if (tok.Length > 0) await pet.Face(tok[0]);
                        break;
                    case "stop": await pet.Stop(); break;
                    // unknown ops are ignored (forward-compatible)
                }
            }
            catch { /* a single bad step shouldn't abort the whole command */ }
        }

        await ApplyReaction(m);
        string text = says.Count > 0 ? string.Join("\n", says) : (m.Help ?? "");
        return CommandResult.Ok(text, holdSeconds: m.HoldSeconds);
    }

    // Script execution lives in CommandScriptHost (sandboxed). Replaced with the real runner when the
    // scripting engine is present; this guard keeps the surface stable and the failure friendly.
    private Task<CommandResult> RunScript(CommandManifest m, string dir, string args, CancellationToken ct)
    {
        if (!_scriptsEnabled())
            return Task.FromResult(CommandResult.Error($"/{m.Name}: user scripts are disabled (Settings → enable scripts)."));
        // Network is gated per command: it needs a declared hosts: allowlist AND a standing user approval
        // for exactly that allowlist (keyed by the command's CommandStore id = its folder name).
        string id = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool networkApproved = m.Hosts.Count > 0 && _networkApproved(id, m.Hosts);
        return CommandScriptHost.RunAsync(m, args, _pet(), networkApproved, ct);
    }

    // -------------------------------------------------------------------- helpers
    private async Task ApplyReaction(CommandManifest m)
    {
        var (expr, secs) = m.PickReaction();
        if (string.IsNullOrEmpty(expr)) return;
        var pet = _pet();
        if (pet is null) return;
        try { await pet.Expression(expr!, secs ?? DefaultExpressionSeconds); }
        catch { /* the character may lack that expression — ignore */ }
    }

    /// <summary>Resolve an image reference to a loadable URI. <c>avares://</c> / <c>http(s)://</c> pass
    /// through; anything else is treated as folder-relative and confined to <paramref name="dir"/> (no
    /// <c>..</c>, no rooted paths) before being returned as a <c>file://</c> URI. Null if it can't be
    /// resolved or escapes the command's own folder.</summary>
    private static string? ResolveImage(string image, string dir)
    {
        image = image.Trim();
        if (image.Length == 0) return null;
        if (image.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
            || image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return image;

        if (Path.IsPathRooted(image) || image.Contains("..")) return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(dir, image));
            string root = Path.GetFullPath(dir);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(full)) return null;
            return new Uri(full).AbsoluteUri;
        }
        catch { return null; }
    }

    private static bool TryD(string s, out double d) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
}
