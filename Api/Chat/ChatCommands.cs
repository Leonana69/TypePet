using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>The outcome of a slash command, displayed exactly like a chat reply: <see cref="Text"/> is
/// spoken by the pet (and shown in history when it's open), <see cref="Sources"/> are optional links shown
/// in history, and <see cref="IsError"/> tints the history bubble. <see cref="Link"/> is a single primary
/// link surfaced as a clickable button IN THE PET'S SPEECH BUBBLE too (the say bar passes it to Say); it's
/// also typically included in <see cref="Sources"/> so it appears in history. <see cref="ImageUrl"/> is an
/// optional image (e.g. the character canvas) shown in both the pet's bubble and the history card.
/// <see cref="ClipboardText"/>, when set, is written to the system clipboard by the say bar (which owns a
/// <c>TopLevel</c>); the command stays UI-free and only carries the text to copy.</summary>
public sealed record CommandResult(
    string Text, IReadOnlyList<WebSource> Sources, bool IsError, WebSource? Link = null, string? ImageUrl = null,
    string? ClipboardText = null)
{
    public static CommandResult Ok(string text, IReadOnlyList<WebSource>? sources = null,
        WebSource? link = null, string? imageUrl = null, string? clipboardText = null)
        => new(text, sources ?? Array.Empty<WebSource>(), false, link, imageUrl, clipboardText);

    public static CommandResult Error(string text) => new(text, Array.Empty<WebSource>(), true);
}

/// <summary>
/// Slash-command handling for the input bar. Commands are deterministic and run <b>without the LLM</b>
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
/// </summary>
public sealed class ChatCommands
{
    /// <summary>The leading character that marks a message as a command.</summary>
    public const char Prefix = '/';

    private readonly NexonGmsRankApi _gms = new();
    private readonly MapleGgScraper _mapleGg = new();
    private readonly MapleKitApi _mapleKit = new();
    private readonly IReadOnlyList<Command> _commands;

    public ChatCommands()
    {
        _commands = new[]
        {
            new Command("rank", $"/rank [{RankServers.FlagList.Replace(", ", "|")}] <character>",
                "Look up a MapleStory character by server: -na/-eu = GMS (default -na, with a global rank), -kr = KMS, -sea = MSEA, -tw = TMS.", RankAsync),
            new Command("ssc", "/ssc", "Copy \"Sacred Symbol/claim\" to the clipboard.", Copy("Sacred Symbol/claim")),
            new Command("asc", "/asc", "Copy \"Arcane Symbol/claim\" to the clipboard.", Copy("Arcane Symbol/claim")),
            new Command("help", "/help", "List the available commands.", HelpAsync),
        };
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
        lines.Add(r.World);
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
        lines.Add(m.World);
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
        lines.Add(g.World);
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

    private sealed record Command(
        string Name, string Usage, string Help, Func<string, CancellationToken, Task<CommandResult>> Run);
}
