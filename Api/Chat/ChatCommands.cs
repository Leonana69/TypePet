using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using MaplePet.Api;

namespace MaplePet.Api.Chat;

/// <summary>The outcome of a slash command, displayed exactly like a chat reply: <see cref="Text"/> is
/// spoken by the pet (and shown in history when it's open), <see cref="Sources"/> are optional links shown
/// in history, and <see cref="IsError"/> tints the history bubble. <see cref="Link"/> is a single primary
/// link surfaced as a clickable button IN THE PET'S SPEECH BUBBLE too (the say bar passes it to Say); it's
/// also typically included in <see cref="Sources"/> so it appears in history. <see cref="ImageUrl"/> is an
/// optional image (e.g. the character canvas) shown in both the pet's bubble and the history card.
/// <see cref="ClipboardText"/>, when set, is written to the system clipboard by the say bar (which owns a
/// <c>TopLevel</c>); the command stays UI-free and only carries the text to copy. <see cref="HoldSeconds"/>
/// overrides how long the pet holds the result bubble (and stays still) — null uses the say bar's default.</summary>
public sealed record CommandResult(
    string Text, IReadOnlyList<WebSource> Sources, bool IsError, WebSource? Link = null, string? ImageUrl = null,
    string? ClipboardText = null, double? HoldSeconds = null)
{
    public static CommandResult Ok(string text, IReadOnlyList<WebSource>? sources = null,
        WebSource? link = null, string? imageUrl = null, string? clipboardText = null, double? holdSeconds = null)
        => new(text, sources ?? Array.Empty<WebSource>(), false, link, imageUrl, clipboardText, holdSeconds);

    public static CommandResult Error(string text) => new(text, Array.Empty<WebSource>(), true);
}

/// <summary>
/// Slash-command handling for the input bar. Most commands are deterministic and run <b>without the LLM</b>
/// — so they work even when the chatbot is off or unconfigured. A message that begins with '/' is parsed
/// into a command name + argument string and dispatched here; the <see cref="CommandResult"/> is then
/// shown the same way as a chat reply (the pet speaks it; it's added to history if the panel is open).
/// Adding a command is a one-line edit to the table in the constructor.
///
/// First command: <c>/rank [-na|-eu|-kr|-sea|-tw] &lt;character&gt;</c> — looks a MapleStory character up. The
/// server is picked per call by a leading flag (no setting); the flag decides the keyless source (see
/// <see cref="RankServers"/>): <c>-na</c>/<c>-eu</c> = GMS via the public rankings endpoint
/// (<see cref="NexonGmsRankApi"/>, default <c>-na</c>, with a true global rank), <c>-kr</c>/<c>-sea</c> =
/// KMS/MSEA via the maple.gg profile scrape (<see cref="MapleGgScraper"/>), <c>-tw</c> = TMS via the
/// keyless maple-kit proxy (<see cref="MapleKitApi"/>).
///
/// The one exception to "no LLM" is <c>/fortune</c>, which asks the active chat provider to play a Maple
/// World oracle — so it needs the chatbot enabled and a configured provider (injected as
/// <c>chatEnabled</c> / <c>buildConfig</c>) and reports a friendly error if either is missing.
/// </summary>
public sealed class ChatCommands
{
    /// <summary>The leading character that marks a message as a command.</summary>
    public const char Prefix = '/';

    private readonly NexonGmsRankApi _gms = new();
    private readonly MapleGgScraper _mapleGg = new();
    private readonly MapleKitApi _mapleKit = new();
    private readonly Func<bool> _chatEnabled;
    private readonly Func<ChatSessionConfig?> _buildConfig;
    private readonly Func<IPetControl?> _pet;
    private readonly IReadOnlyList<Command> _commands;

    /// <summary>UI-facing metadata for every registered command (name, usage, help), in declared order.
    /// Used by the say bar to populate its '/' command dropdown; the handler delegates stay private.</summary>
    public IReadOnlyList<CommandInfo> Commands { get; }

    /// <param name="chatEnabled">Whether the chatbot is turned on (Settings → Chatbot). Only the LLM-backed
    /// <c>/fortune</c> consults it; the keyless commands ignore it.</param>
    /// <param name="buildConfig">Resolves the active provider into a ready chat session (backend + model +
    /// key), or null when nothing usable is configured — used by <c>/fortune</c> to call the model.</param>
    /// <param name="pet">The live pet control, or null when not ready. <c>/fortune</c> reads its available
    /// expressions (to offer the model) and makes the pet wear the one the oracle picks.</param>
    public ChatCommands(Func<bool> chatEnabled, Func<ChatSessionConfig?> buildConfig, Func<IPetControl?> pet)
    {
        _chatEnabled = chatEnabled;
        _buildConfig = buildConfig;
        _pet = pet;
        _commands = new[]
        {
            new Command("rank", $"/rank [{RankServers.FlagList.Replace(", ", "|")}] <character>",
                "Look up a MapleStory character by server: -na/-eu = GMS (default -na, with a global rank), -kr = KMS, -sea = MSEA, -tw = TMS.", RankAsync),
            new Command("fortune", "/fortune [name]",
                "Have the Maple World oracle read your daily luck (needs the chatbot enabled + configured).", FortuneAsync),
            new Command("ssc", "/ssc", "Copy \"Sacred Symbol/claim\" to the clipboard.", Copy("Sacred Symbol/claim")),
            new Command("asc", "/asc", "Copy \"Arcane Symbol/claim\" to the clipboard.", Copy("Arcane Symbol/claim")),
            new Command("esfera", "/esfera", "Show the Esfera guide image.", EsferaAsync),
            new Command("help", "/help", "List the available commands.", HelpAsync),
        };
        Commands = _commands.Select(c => new CommandInfo(c.Name, c.Usage, c.Help)).ToArray();
    }

    /// <summary>True if <paramref name="text"/> is a slash command (first non-space character is '/').</summary>
    public static bool IsCommand(string? text)
        => !string.IsNullOrEmpty(text) && text.TrimStart().StartsWith(Prefix);

    /// <summary>Parse and run a slash command. Never throws — every failure comes back as an error
    /// result so the caller (the say bar) can show it like any other reply.</summary>
    public async Task<CommandResult> RunAsync(string input, CancellationToken ct)
    {
        string s = (input ?? "").Trim();
        if (s.StartsWith(Prefix)) s = s[1..].TrimStart();
        if (s.Length == 0) return Unknown("");

        int sp = s.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        string name = (sp < 0 ? s : s[..sp]).ToLowerInvariant();
        string args = sp < 0 ? "" : s[(sp + 1)..].Trim();

        var cmd = _commands.FirstOrDefault(c => c.Name == name);
        if (cmd is null) return Unknown(name);

        try { return await cmd.Run(args, ct).ConfigureAwait(false); }
        catch (Exception ex) { return CommandResult.Error($"/{name} failed: {ex.Message}"); }
    }

    // ---- commands ----------------------------------------------------------------

    private async Task<CommandResult> RankAsync(string args, CancellationToken ct)
    {
        // A leading flag (e.g. "-kr Name") picks the server; with none, it defaults to -na (GMS NA). Names
        // are alphanumeric/CJK, so a leading '-' is always a flag, never part of the name.
        var (flag, rest) = SplitLeadingFlag(args);
        var server = flag is null ? RankServers.Default : RankServers.Resolve(flag);
        if (server is null)
            return CommandResult.Error($"Unknown flag \"-{flag}\" — use {RankServers.FlagList}.");
        if (rest.Length == 0)
            return CommandResult.Error($"Usage: /rank [{RankServers.FlagList.Replace(", ", "|")}] <character name>");

        try
        {
            // Each source returns its own shape (and a keyless render image / profile link); dispatch by kind.
            switch (server.Kind)
            {
                case RankSourceKind.Gms:
                {
                    // GMS uniquely returns a true global rank position; ServerCode is the na/eu sub-server.
                    var g = await _gms.GetRankAsync(rest, server.DataUrl, server.ServerCode, ct).ConfigureAwait(false);
                    return CommandResult.Ok(FormatGmsRank(g, server), link: BuildInfoLink(server, g.Name), imageUrl: g.ImageUrl);
                }
                case RankSourceKind.MapleGg:
                {
                    // KMS/MSEA: scrape the public maple.gg page for identity, plus a best-effort dak.gg API
                    // call (server.StatsUrl) for EXP%/rank/Legion.
                    var m = await _mapleGg.GetRankAsync(rest, server.DataUrl, server.StatsUrl, ct).ConfigureAwait(false);
                    return CommandResult.Ok(FormatMapleGgRank(m, server), link: BuildInfoLink(server, m.Name), imageUrl: m.ImageUrl);
                }
                case RankSourceKind.MapleKit:
                {
                    // TMS: the keyless maple-kit proxy returns the full Open-API profile in one call.
                    var r = await _mapleKit.GetRankAsync(rest, server.DataUrl, ct).ConfigureAwait(false);
                    return CommandResult.Ok(FormatRank(r, server), link: BuildInfoLink(server, r.Name), imageUrl: r.ImageUrl);
                }
                default:
                    return CommandResult.Error("Unsupported server.");
            }
        }
        catch (RankException ex)
        {
            return CommandResult.Error(ex.Message); // already user-facing
        }
    }

    /// <summary>Split an optional leading <c>-flag</c>/<c>--flag</c> token off the front of <paramref
    /// name="args"/>. Returns the lowercased flag name (no dashes) or null, plus the remaining text trimmed.
    /// When there's no flag, the flag is null and the remainder is the full trimmed input.</summary>
    private static (string? flag, string rest) SplitLeadingFlag(string args)
    {
        string s = (args ?? "").Trim();
        if (s.Length == 0 || s[0] != '-') return (null, s);
        int sp = s.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        string token = sp < 0 ? s : s[..sp];
        string rest = sp < 0 ? "" : s[(sp + 1)..].Trim();
        return (token.TrimStart('-').ToLowerInvariant(), rest);
    }

    /// <summary>The bundled fortune-teller system prompt (an <c>avares:</c> app resource compiled into the
    /// assembly). Read on demand via <see cref="LoadFortunePrompt"/>; placeholders are filled per call.</summary>
    private const string FortunePromptUri = "avares://MaplePet/Assets/Program/Prompts/fortune_teller.md";

    /// <summary>How long the fortune (speech bubble + the matching pet expression) stays on screen.</summary>
    private const double FortuneHoldSeconds = 60;

    /// <summary>The luck tiers the oracle can roll, weighted MapleStory-style: ordinary days are common, a
    /// boom day stings now and then, and a Legendary jackpot is rare. The SYSTEM rolls this (not the model)
    /// — the prompt states the tier is "provided by the system" so the reply just narrates the result.</summary>
    private static readonly (string Tier, int Weight)[] LuckTiers =
    {
        ("Boom", 10), ("Rare", 35), ("Epic", 30), ("Unique", 18), ("Legendary", 7),
    };

    /// <summary>Per-tier face candidates, tried in order as a fallback when the model didn't name a usable
    /// expression. Filtered against the character's actual expressions, so names it lacks are skipped — these
    /// are the common MapleStory face names, but any that don't exist for the worn character are ignored.</summary>
    private static readonly Dictionary<string, string[]> TierExpressions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boom"] = new[] { "despair", "cry", "troubled", "pain", "stunned", "hit" },
        ["Rare"] = new[] { "blink", "hum", "smile" },
        ["Epic"] = new[] { "smile", "hum", "cheers" },
        ["Unique"] = new[] { "cheers", "love", "glitter", "smile" },
        ["Legendary"] = new[] { "cheers", "glitter", "shine", "love", "wink", "smile" },
    };

    /// <summary><c>/fortune [name]</c> — the one LLM-backed command. It asks the active chat provider to
    /// read the user's daily MapleStory "luck" using the bundled oracle persona. Unlike the keyless
    /// commands it requires the chatbot ON and a configured provider, so it checks both up front and
    /// returns a friendly error if either is missing. The luck tier is rolled here (not by the model); the
    /// oracle also picks a facial expression from the worn character's set, which the pet then wears for as
    /// long as the spoken fortune is held. An optional argument names the Mapler (defaults to "Mapler").</summary>
    private async Task<CommandResult> FortuneAsync(string args, CancellationToken ct)
    {
        if (!_chatEnabled())
            return CommandResult.Error("🔮 The crystal ball is dark — enable the chatbot in Settings → Chatbot to read your fortune.");

        var cfg = _buildConfig();
        if (cfg is null)
            return CommandResult.Error("🔮 No chat provider is configured — add an API key in Settings → Chatbot to read your fortune.");

        string? template = LoadFortunePrompt();
        if (template is null)
            return CommandResult.Error("Couldn't load the fortune-teller prompt.");

        // The oracle picks a face for the pet to wear, so it can only choose from what THIS character
        // supports. Read the live capability snapshot (best-effort: the pet may not be ready yet).
        var pet = _pet();
        IReadOnlyList<string> expressions = Array.Empty<string>();
        if (pet is not null)
        {
            try { expressions = (await pet.GetCapabilities()).Expressions.Select(e => e.Name).ToArray(); }
            catch { /* pet not ready — carry on without a chosen expression */ }
        }

        string tier = RollLuckTier();
        string name = args.Trim();
        if (name.Length == 0) name = "Mapler";

        string system = template
            .Replace("{{luck_tier}}", tier)
            .Replace("{{user_name}}", name)
            .Replace("{{date}}", DateTime.Now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture))
            .Replace("{{expressions}}", expressions.Count > 0 ? string.Join(", ", expressions) : "(none)");

        // One-shot call: the persona lives entirely in the system prompt and we offer no tools, so the model
        // just returns the fortune text (no agent loop needed).
        var req = new ChatRequest(
            system,
            new[] { ChatMessage.User("Read my fortune for today.") },
            Array.Empty<ChatToolDef>(),
            cfg.Model,
            cfg.MaxTokens);

        ChatTurn turn = await cfg.Backend.SendAsync(req, ct).ConfigureAwait(false);

        // The reply leads with an "Expression: <name>" line. Pull it out (and ALWAYS strip it so it never
        // shows in the spoken bubble), then fall back to a tier-appropriate face if the model omitted it or
        // named one this character lacks.
        var (chosen, text) = ExtractExpression(turn.Text ?? "", expressions);
        chosen ??= FallbackExpression(tier, expressions);
        if (text.Length == 0)
            return CommandResult.Error("🔮 The oracle is silent right now — try again in a moment.");

        // Make the pet physically wear the divined mood for as long as the fortune is shown.
        if (pet is not null && chosen is not null)
            _ = pet.Expression(chosen, FortuneHoldSeconds);

        // Hold the fortune on screen for a minute so the whole reading can be savoured.
        return CommandResult.Ok(text, holdSeconds: FortuneHoldSeconds);
    }

    /// <summary>Pick a luck tier by weight (see <see cref="LuckTiers"/>).</summary>
    private static string RollLuckTier()
    {
        int total = LuckTiers.Sum(t => t.Weight);
        int roll = Random.Shared.Next(total);
        foreach (var (tier, weight) in LuckTiers)
        {
            if (roll < weight) return tier;
            roll -= weight;
        }
        return LuckTiers[0].Tier; // unreachable: roll is always < total
    }

    /// <summary>Split the model's reply into (chosen expression, spoken text). It finds a line like
    /// <c>Expression: smile</c> anywhere in the reply, removes it from the spoken text regardless, and
    /// returns the named expression only when it matches one the character actually has (case-insensitive,
    /// so the stray directive never leaks into the bubble even if the name is unknown).</summary>
    private static (string? expression, string text) ExtractExpression(string raw, IReadOnlyCollection<string> available)
    {
        var kept = new List<string>();
        string? picked = null;
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*expression\s*[:=]\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (picked is null && m.Success)
            {
                string val = m.Groups[1].Value.Trim().Trim('[', ']', '"', '\'', '*', '.');
                picked = available.FirstOrDefault(n => string.Equals(n, val, StringComparison.OrdinalIgnoreCase));
                continue; // drop the machine-readable line whether or not the name was valid
            }
            kept.Add(line);
        }
        return (picked, string.Join("\n", kept).Trim());
    }

    /// <summary>Pick a face matching the tier from <see cref="TierExpressions"/>, falling back to "smile" or
    /// any available expression. Null only when the character exposes no expressions at all.</summary>
    private static string? FallbackExpression(string tier, IReadOnlyCollection<string> available)
    {
        if (available.Count == 0) return null;
        if (TierExpressions.TryGetValue(tier, out var prefs))
            foreach (var p in prefs)
            {
                var m = available.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
                if (m is not null) return m;
            }
        return available.FirstOrDefault(n => string.Equals(n, "smile", StringComparison.OrdinalIgnoreCase))
               ?? available.First();
    }

    /// <summary>Read the bundled fortune-teller prompt template, or null if it can't be loaded.</summary>
    private static string? LoadFortunePrompt()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri(FortunePromptUri));
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    private Task<CommandResult> HelpAsync(string args, CancellationToken ct)
    {
        string list = string.Join("\n", _commands.Select(c => $"{c.Usage} — {c.Help}"));
        return Task.FromResult(CommandResult.Ok("Commands:\n" + list));
    }

    /// <summary>Builds a no-argument command that puts a fixed string on the system clipboard. The handler is
    /// pure — it just returns the text to copy (via <see cref="CommandResult.ClipboardText"/>) plus the line
    /// the pet speaks; the say bar does the actual clipboard write since it owns a <c>TopLevel</c>.</summary>
    private static Func<string, CancellationToken, Task<CommandResult>> Copy(string text)
        => (_, _) => Task.FromResult(CommandResult.Ok($"Copied \"{text}\" to clipboard 📋", clipboardText: text));

    /// <summary>The bundled Esfera guide image, shown by <c>/esfera</c>. An <c>avares:</c> app-resource URI
    /// (not a network URL) that the image cache loads straight from the bundle; both the pet's speech bubble
    /// and the history card display it (whole, not center-cropped — see <c>ImageCache.IsBundledAsset</c>).</summary>
    private const string EsferaImage = "avares://MaplePet/Assets/Program/esfera.png";

    private Task<CommandResult> EsferaAsync(string args, CancellationToken ct)
        => Task.FromResult(CommandResult.Ok("Esfera guide", imageUrl: EsferaImage, holdSeconds: 120));

    // ---- helpers -----------------------------------------------------------------

    /// <summary>The per-server "check more info on …" link to that server's community profile site (maple.gg
    /// for KMS/MSEA, maple-kit.com for TMS, MapleRanks for GMS). Shown as a clickable line in the pet's bubble
    /// and the history card (not as a citation/source).</summary>
    private static WebSource? BuildInfoLink(RankServer server, string characterName)
    {
        string url = string.Format(server.InfoUrlFormat, Uri.EscapeDataString(characterName));
        return new WebSource($"Check more info on {server.InfoSite} ↗", url, "MapleStory character profile");
    }

    private CommandResult Unknown(string name)
    {
        string known = string.Join(", ", _commands.Select(c => Prefix + c.Name));
        string head = string.IsNullOrEmpty(name) ? "Type a command after '/'." : $"Unknown command \"/{name}\".";
        return CommandResult.Error($"{head} Try: {known}");
    }

    // TMS (maple-kit): the proxy returns EXP%, the global rank and the Legion (Union) level + grade.
    private static string FormatRank(MapleKitApi.CharacterRank r, RankServer server)
    {
        var lines = new List<string> { $"{r.Name} · Lv.{r.Level} · {r.Class}" };
        if (r.ExpPercent is double pct) lines.Add(ExpBar(pct));
        lines.Add($"World: {r.World}");
        lines.Add(r.Rank is long rk ? $"{server.Label} · Rank #{rk:N0}" : server.Label);
        if (r.Guild is not null) lines.Add($"Guild: {r.Guild}");
        if (r.UnionLevel is int ul && ul > 0)
            lines.Add($"Legion Lv.{ul:N0}" + (r.UnionGrade is { } grade ? $" · {grade}" : ""));
        if (r.Popularity is int pop) lines.Add($"Fame {pop}");
        return string.Join("\n", lines);
    }

    // KMS/MSEA (maple.gg): identity from the page scrape; EXP% (most recent EXP-history point), rank and
    // Legion from the best-effort dak.gg API (null when unavailable — e.g. MSEA omits rank/Legion).
    private static string FormatMapleGgRank(MapleGgScraper.MapleGgRank m, RankServer server)
    {
        var lines = new List<string> { $"{m.Name} · Lv.{m.Level} · {m.Class}" };
        if (m.ExpPercent is double pct) lines.Add(ExpBar(pct));
        lines.Add($"World: {m.World}");
        lines.Add(m.Rank is long rk ? $"{server.Label} · Rank #{rk:N0}" : server.Label);
        if (m.Guild is not null) lines.Add($"Guild: {m.Guild}");
        if (m.LegionLevel is int legion && legion > 0) lines.Add($"Legion Lv.{legion:N0}");
        if (m.Popularity is int pop) lines.Add($"Fame {pop}");
        return string.Join("\n", lines);
    }

    private static string FormatGmsRank(NexonGmsRankApi.GmsRank g, RankServer server)
    {
        var lines = new List<string>
        {
            $"{g.Name} · Lv.{g.Level} · {g.Job}",
        };
        // EXP progress through the current level, as a text bar (omitted at the level cap, where it's null).
        if (g.ExpPercent is double pct) lines.Add(ExpBar(pct));
        lines.Add($"World: {g.World}");
        // Rank is the command's headline and GMS's unique offering (the other servers don't expose a global
        // rank). If it's somehow missing (degenerate row), show the server alone rather than a bogus "#0".
        lines.Add(g.Rank > 0 ? $"{server.Label} · Rank #{g.Rank:N0}" : server.Label);
        // Legion level is present only when the looked-up character is its account's Legion representative
        // (its highest-level character); otherwise it's 0 and the line is omitted.
        if (g.LegionLevel > 0) lines.Add($"Legion Lv.{g.LegionLevel:N0}");
        return string.Join("\n", lines);
    }

    /// <summary>A fixed-width EXP progress bar from a 0–100 percentage, drawn with full/empty block cells
    /// (e.g. <c>EXP ██████░░░░ 60.05%</c>) since the bubble renders the result as plain text.</summary>
    private static string ExpBar(double pct)
    {
        const int width = 10;
        int filled = (int)Math.Round(pct / 100.0 * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return $"EXP {new string('█', filled)}{new string('░', width - filled)} {pct:0.00}%";
    }

    /// <summary>Public, read-only view of a command for the say bar's dropdown — the same name, usage, and
    /// help as <see cref="Command"/>, but without the handler delegate.</summary>
    public sealed record CommandInfo(string Name, string Usage, string Help);

    private sealed record Command(
        string Name, string Usage, string Help, Func<string, CancellationToken, Task<CommandResult>> Run);
}
