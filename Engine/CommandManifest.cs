using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaplePet.Engine;

/// <summary>What a user command does. The kind selects which payload of <see cref="CommandManifest"/> is
/// read and how the interpreter turns it into a result. <see cref="Unknown"/> is an unrecognized kind
/// string — such commands are skipped (and surfaced as invalid) rather than run.</summary>
public enum CommandKind { Text, Clipboard, Image, Link, Prompt, Pet, Script, Unknown }

/// <summary>
/// A user-authored command, parsed from a <c>command.md</c> file: a small <c>---</c>-fenced frontmatter
/// block of <c>key: value</c> metadata plus a markdown <see cref="Body"/>. The body is the *content* —
/// the system prompt for <see cref="CommandKind.Prompt"/>, the script source for
/// <see cref="CommandKind.Script"/>, or one step per line for <see cref="CommandKind.Pet"/>. This is the
/// "Claude-skill" shape: prose lives in the body with no JSON escaping.
///
/// Pure data with no Avalonia / chat dependency (it lives in Engine). <c>CommandInterpreter</c> (in
/// Api/Chat) turns a manifest into a runnable command that produces a <c>CommandResult</c>.
/// </summary>
public sealed class CommandManifest
{
    /// <summary>The command word (no leading '/'), lowercased. e.g. <c>ssc</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Alternate names that also invoke this command (lowercased).</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>The usage line shown in the dropdown / <c>/help</c> (defaults to <c>/name</c>).</summary>
    public string? Usage { get; set; }

    /// <summary>One-line description shown in the dropdown / <c>/help</c>.</summary>
    public string? Help { get; set; }

    public CommandKind Kind { get; set; } = CommandKind.Unknown;

    /// <summary>The original (unrecognized) kind string, kept for a helpful error message.</summary>
    public string? RawKind { get; set; }

    /// <summary>Override for how long the pet holds the result bubble (seconds); null uses the default.</summary>
    public double? HoldSeconds { get; set; }

    /// <summary>Optional facial expression(s) the pet wears after the command runs (any kind). When more than
    /// one is listed (<c>reaction: [smile|30, blink|20]</c>) one is chosen at random each run; a lone
    /// <c>reaction: smile|6</c> is just a one-element list. Each option carries its own optional hold time in
    /// seconds (the number after <c>|</c>); null falls back to a fixed default (10 s — see
    /// <c>CommandInterpreter.DefaultExpressionSeconds</c>), independent of the bubble's <see cref="HoldSeconds"/>.</summary>
    public List<(string Expression, double? Seconds)> Reactions { get; set; } = new();

    // ---- kind payloads (exactly one is read per Kind) ----
    public string? Clipboard { get; set; }        // kind=clipboard: text to copy
    public string? Image { get; set; }            // kind=image: folder-relative file | avares:// | http(s)://
    public string? LinkTitle { get; set; }        // kind=link
    public string? LinkUrl { get; set; }
    public string? Text { get; set; }             // kind=text: optional spoken text (else the body)
    public bool RequiresChat { get; set; } = true; // kind=prompt: needs the chatbot enabled + configured

    /// <summary>kind=prompt: a weighted set of options for the <c>{{roll}}</c> token, so the system (not
    /// the model) picks an outcome with controlled odds — e.g. <c>roll: Boom:10, Rare:35, Epic:30</c>.</summary>
    public List<(string Value, int Weight)> Roll { get; set; } = new();

    /// <summary>kind=script: the network allowlist — hostnames the script may reach via <c>httpGet</c>
    /// (declared <c>hosts: a.com, b.com</c>). Empty ⇒ no network at all. A leaf entry also covers its
    /// subdomains (<c>maple.gg</c> ⇒ <c>msea.maple.gg</c>). The capability is still gated on the user's
    /// scripts toggle AND an explicit per-command approval (see <c>Settings.NetworkApprovedCommands</c>).</summary>
    public List<string> Hosts { get; set; } = new();

    /// <summary>The markdown body after the frontmatter (prompt text / script source / pet steps).</summary>
    public string Body { get; set; } = "";

    /// <summary>The command word plus any aliases (non-empty).</summary>
    public IEnumerable<string> AllNames() =>
        new[] { Name }.Concat(Aliases).Where(n => !string.IsNullOrEmpty(n));

    /// <summary>A stable signature of the (sanitized) host allowlist — sorted + joined — used to detect when
    /// a command's declared <c>hosts:</c> change so a stored network approval is revoked until re-granted.
    /// The instance form mirrors <see cref="Hosts"/>; the static form lets callers sign an arbitrary list.</summary>
    public string HostsSignature() => HostsSignature(Hosts);

    public static string HostsSignature(IEnumerable<string>? hosts) =>
        string.Join(",", (hosts ?? Enumerable.Empty<string>())
            .Select(h => (h ?? "").Trim().ToLowerInvariant())
            .Where(h => h.Length > 0).Distinct().OrderBy(h => h, StringComparer.Ordinal));

    /// <summary>Normalize fields: lowercase names, default the usage, drop bad hold times.</summary>
    public void Sanitize()
    {
        Name = (Name ?? "").Trim().ToLowerInvariant();
        Aliases = (Aliases ?? new()).Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct().ToList();
        Usage = string.IsNullOrWhiteSpace(Usage) ? "/" + Name : Usage!.Trim();
        Help = (Help ?? "").Trim();
        if (HoldSeconds is { } h && (!double.IsFinite(h) || h <= 0)) HoldSeconds = null;
        // Keep only plausible bare hostnames (letters/digits/'.'/'-', with a dot), lowercased + de-duped. A
        // scheme, path, port or wildcard is dropped rather than half-honored, so the runtime allowlist check
        // is a clean host comparison.
        Hosts = (Hosts ?? new()).Select(x => (x ?? "").Trim().ToLowerInvariant())
            .Where(IsValidHost).Distinct().ToList();
    }

    /// <summary>A bare hostname for the <c>hosts:</c> allowlist: ASCII letters/digits/'.'/'-' only (an IDN must
    /// be given in punycode <c>xn--…</c>), at least one dot, no scheme/slash/port/'*' and no leading/trailing
    /// dot. ASCII-only so a stored entry matches the punycode <c>Uri.IdnHost</c> the fetch actually resolves.</summary>
    public static bool IsValidHost(string h) =>
        h.Length is > 0 and <= 253 && h.Contains('.') && h[0] != '.' && h[^1] != '.'
        && h.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');

    /// <summary>Null when the manifest is runnable, otherwise a short reason it isn't (shown in the UI).</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "missing 'name'";
        if (!IsValidName(Name)) return $"invalid name \"{Name}\" (use letters, digits, - or _)";
        return Kind switch
        {
            CommandKind.Unknown => $"unknown kind \"{RawKind}\"",
            CommandKind.Clipboard when string.IsNullOrEmpty(Clipboard) => "clipboard kind needs a 'clipboard:' value",
            CommandKind.Image when string.IsNullOrWhiteSpace(Image) => "image kind needs an 'image:' value",
            CommandKind.Link when string.IsNullOrWhiteSpace(LinkUrl) => "link kind needs a 'link:' value",
            CommandKind.Prompt when string.IsNullOrWhiteSpace(Body) => "prompt kind needs a body (the prompt)",
            CommandKind.Script when string.IsNullOrWhiteSpace(Body) => "script kind needs a body (the script)",
            CommandKind.Pet when string.IsNullOrWhiteSpace(Body) => "pet kind needs step lines in the body",
            _ => null,
        };
    }

    /// <summary>A safe command word: 1–32 chars of letters/digits/'-'/'_'. Mirrors the store's path guard.</summary>
    public static bool IsValidName(string n) =>
        n.Length is > 0 and <= 32 && n.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    // -------------------------------------------------------------------- parsing
    /// <summary>Parse a <c>command.md</c> file (frontmatter + body). Returns the manifest, or null plus a
    /// human-readable error. Never throws.</summary>
    public static (CommandManifest? manifest, string? error) Parse(string? fileText)
    {
        if (string.IsNullOrWhiteSpace(fileText)) return (null, "empty file");

        var lines = fileText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        if (i < lines.Length && lines[i].Length > 0 && lines[i][0] == '﻿') lines[i] = lines[i][1..]; // BOM
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || lines[i].Trim() != "---")
            return (null, "missing '---' frontmatter header");

        i++; // past the opening fence
        var fm = new List<string>();
        int close = -1;
        for (; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { close = i; break; }
            fm.Add(lines[i]);
        }
        if (close < 0) return (null, "missing closing '---' for the frontmatter");

        string body = string.Join("\n", lines.Skip(close + 1)).Trim('\n');
        var m = new CommandManifest { Body = body };
        foreach (var raw in fm)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string val = StripQuotes(line[(colon + 1)..].Trim());
            Apply(m, key, val);
        }
        m.Kind = ParseKind(m.RawKind);
        return (m, null);
    }

    private static void Apply(CommandManifest m, string key, string val)
    {
        switch (key)
        {
            case "name": m.Name = val; break;
            case "aliases": m.Aliases = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
            case "usage": m.Usage = val; break;
            case "help": case "description": m.Help = val; break;
            case "kind": m.RawKind = val; break;
            case "holdseconds": case "hold": if (TryDouble(val, out var hs)) m.HoldSeconds = hs; break;
            case "reaction": m.Reactions = ParseReactions(val); break;
            case "clipboard": case "copy": m.Clipboard = val; break;
            case "image": m.Image = val; break;
            case "text": case "say": m.Text = val; break;
            case "link": (m.LinkTitle, m.LinkUrl) = ParseLink(val); break;
            case "requireschat": m.RequiresChat = !IsFalsey(val); break;
            case "roll": m.Roll = ParseRoll(val); break;
            case "hosts": case "host":
                m.Hosts = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(h => h.ToLowerInvariant()).ToList();
                break;
        }
    }

    /// <summary>Pick a value from <see cref="Roll"/> by weight (for the <c>{{roll}}</c> token), or "" when
    /// no roll is declared.</summary>
    public string PickRoll()
    {
        if (Roll.Count == 0) return "";
        int total = Roll.Sum(r => r.Weight);
        if (total <= 0) return Roll[0].Value;
        int roll = Random.Shared.Next(total);
        foreach (var (val, w) in Roll)
        {
            if (roll < w) return val;
            roll -= w;
        }
        return Roll[0].Value;
    }

    /// <summary>Pick a reaction face at random (uniform) from <see cref="Reactions"/>, or <c>(null, null)</c>
    /// when none is declared. The list form (<c>reaction: [a, b|6]</c>) varies the face each run; a single
    /// <c>reaction: a</c> always returns that one.</summary>
    public (string? Expression, double? Seconds) PickReaction()
    {
        if (Reactions.Count == 0) return (null, null);
        var r = Reactions[Random.Shared.Next(Reactions.Count)];
        return (r.Expression, r.Seconds);
    }

    private static List<(string Value, int Weight)> ParseRoll(string val)
    {
        var list = new List<(string, int)>();
        foreach (var part in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.LastIndexOf(':');
            if (colon < 0) { list.Add((part, 1)); continue; }
            string name = part[..colon].Trim();
            int weight = int.TryParse(part[(colon + 1)..].Trim(), out var w) && w > 0 ? w : 1;
            if (name.Length > 0) list.Add((name, weight));
        }
        return list;
    }

    private static CommandKind ParseKind(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "text" or "say" => CommandKind.Text,
        "clipboard" or "copy" => CommandKind.Clipboard,
        "image" or "img" => CommandKind.Image,
        "link" or "url" => CommandKind.Link,
        "prompt" or "llm" or "ai" => CommandKind.Prompt,
        "pet" or "action" => CommandKind.Pet,
        "script" or "js" => CommandKind.Script,
        _ => CommandKind.Unknown,
    };

    /// <summary>Parse a <c>reaction:</c> value into one or more <c>expr|seconds</c> options. A bare value
    /// (<c>smile|6</c>) yields a single option; a bracketed list (<c>[smile|30, blink|20]</c>) yields several,
    /// from which <see cref="PickReaction"/> picks one uniformly at random. The <c>|seconds</c> is the hold
    /// time everywhere (omitted ⇒ default); blank or non-positive durations fall back to null.</summary>
    private static List<(string Expression, double? Seconds)> ParseReactions(string val)
    {
        var list = new List<(string, double?)>();
        if (string.IsNullOrWhiteSpace(val)) return list;
        val = val.Trim();

        // A bracketed value is a comma-separated list of options; a bare value is a single option (so an
        // expression name is never split on a stray comma — only the explicit [..] form lists alternatives).
        if (val.Length >= 2 && val[0] == '[' && val[^1] == ']')
            foreach (var part in val[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddReaction(list, part);
        else
            AddReaction(list, val);

        return list;
    }

    private static void AddReaction(List<(string, double?)> list, string part)
    {
        var seg = part.Split('|', 2);
        string expr = seg[0].Trim();
        if (expr.Length == 0) return;
        double? secs = seg.Length > 1 && TryDouble(seg[1].Trim(), out var s) && double.IsFinite(s) && s > 0 ? s : null;
        list.Add((expr, secs));
    }

    private static (string? title, string? url) ParseLink(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return (null, null);
        // "Title|https://…" or just "https://…"
        int bar = val.IndexOf('|');
        if (bar >= 0) return (val[..bar].Trim(), val[(bar + 1)..].Trim());
        return (null, val.Trim());
    }

    private static string StripQuotes(string v)
    {
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            return v[1..^1];
        return v;
    }

    private static bool IsFalsey(string v) =>
        v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" ||
        v.Equals("no", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase);

    private static bool TryDouble(string v, out double d) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    // -------------------------------------------------------------------- argument substitution
    private static readonly Regex LeftoverPositional = new(@"\{\{\s*\d+\s*\}\}", RegexOptions.Compiled);

    /// <summary>Fill <c>{{args}}</c> (the whole argument string), <c>{{1}}</c>/<c>{{2}}</c>… (whitespace-split
    /// positionals), and any <paramref name="named"/> tokens (e.g. <c>{{name}}</c>, <c>{{date}}</c>) into
    /// <paramref name="template"/>. Plain string replacement — no expression evaluation, so it's safe on
    /// untrusted templates. Unfilled positionals collapse to empty; unknown <c>{{…}}</c> are left literal.</summary>
    public static string Substitute(string? template, string? args, IReadOnlyDictionary<string, string>? named = null)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        args ??= "";
        string s = template.Replace("{{args}}", args);

        var tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
            s = s.Replace("{{" + (i + 1) + "}}", tokens[i]);

        if (named is not null)
            foreach (var kv in named)
                s = s.Replace("{{" + kv.Key + "}}", kv.Value);

        return LeftoverPositional.Replace(s, "");
    }
}
