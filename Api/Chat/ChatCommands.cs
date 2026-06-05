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
/// also typically included in <see cref="Sources"/> so it appears in history.</summary>
public sealed record CommandResult(string Text, IReadOnlyList<WebSource> Sources, bool IsError, WebSource? Link = null)
{
    public static CommandResult Ok(string text, IReadOnlyList<WebSource>? sources = null, WebSource? link = null)
        => new(text, sources ?? Array.Empty<WebSource>(), false, link);

    public static CommandResult Error(string text) => new(text, Array.Empty<WebSource>(), true);
}

/// <summary>
/// Slash-command handling for the input bar. Commands are deterministic and run <b>without the LLM</b>
/// — so they work even when the chatbot is off or unconfigured. A message that begins with '/' is parsed
/// into a command name + argument string and dispatched here; the <see cref="CommandResult"/> is then
/// shown the same way as a chat reply (the pet speaks it; it's added to history if the panel is open).
/// Adding a command is a one-line edit to the table in the constructor.
///
/// First command: <c>/rank &lt;character&gt;</c> — looks a MapleStory character up. The server is chosen in
/// Settings: KMS/SEA/TMS go through the keyed Nexon Open API (<see cref="NexonMapleApi"/>); GMS (NA/EU) has
/// no Open API and uses the keyless public rankings endpoint (<see cref="NexonGmsRankApi"/>), which also
/// returns a true global rank position.
/// </summary>
public sealed class ChatCommands
{
    /// <summary>The leading character that marks a message as a command.</summary>
    public const char Prefix = '/';

    private readonly Func<string?> _nexonKey;
    private readonly Func<string> _region;
    private readonly NexonMapleApi _maple = new();
    private readonly NexonGmsRankApi _gms = new();
    private readonly IReadOnlyList<Command> _commands;

    /// <param name="nexonKey">Live accessor for the Nexon Open API key of the SELECTED region (read per
    /// call, so a key/region edited in Settings applies immediately). Null/empty means "not configured".
    /// Unused for GMS, which is keyless.</param>
    /// <param name="region">Live accessor for the selected MapleStory region id (kms/sea/tms or gms).</param>
    public ChatCommands(Func<string?> nexonKey, Func<string> region)
    {
        _nexonKey = nexonKey;
        _region = region;
        _commands = new[]
        {
            new Command("rank", "/rank [-na|-eu] <character>", "Look up a MapleStory character (server set in Settings): level/class/world — plus a global rank on GMS. On GMS, -na/-eu picks the region (default NA).", RankAsync),
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
        // An optional leading flag (e.g. "-eu Name") picks the GMS sub-server; when absent, `rest` is the
        // whole argument. Names are alphanumeric, so a leading '-' is always a flag, never part of the name.
        var (flag, rest) = SplitLeadingFlag(args);
        var region = NexonMapleApi.ResolveRegion(_region());

        // GMS has no Open API: it uses the keyless public rankings endpoint and (uniquely) returns a true
        // global rank position. Its NA/EU split is selected here by the -na/-eu flag (default NA).
        if (!region.RequiresKey)
        {
            var server = NexonGmsRankApi.DefaultServer;
            if (flag is not null)
            {
                var s = NexonGmsRankApi.ResolveServer(flag);
                if (s is null) return CommandResult.Error($"Unknown flag \"-{flag}\" — use -na or -eu.");
                server = s;
            }
            if (rest.Length == 0) return CommandResult.Error("Usage: /rank [-na|-eu] <character name>");

            try
            {
                var g = await _gms.GetRankAsync(rest, region.BasePath, server.Code, ct).ConfigureAwait(false);
                // MapleRanks has a per-character page for GMS — offer it as a "check more info on" link in
                // both the speech bubble and the history. It's NOT a citation, so it goes in Link (not the
                // "Sources" list). GMS-only, since mapleranks covers GMS.
                var link = new WebSource("Check more info on MapleRanks ↗",
                    $"https://mapleranks.com/u/{Uri.EscapeDataString(g.Name)}", "MapleStory character profile");
                return CommandResult.Ok(FormatGmsRank(g, server), link: link);
            }
            catch (NexonApiException ex)
            {
                return CommandResult.Error(ex.Message); // already user-facing
            }
        }

        // Keyed Open-API path (KMS/SEA/TMS): a single endpoint, so the -na/-eu flag doesn't apply here.
        if (flag is not null)
            return CommandResult.Error($"The -{flag} flag only applies to GMS. Switch the server in Settings → MAPLESTORY.");
        if (rest.Length == 0) return CommandResult.Error("Usage: /rank <character name>");

        string? key = _nexonKey();
        if (string.IsNullOrWhiteSpace(key))
            return CommandResult.Error($"No {region.Label} Nexon API key set. Add it in Settings → MAPLESTORY to use /rank.");

        try
        {
            var r = await _maple.GetRankAsync(rest, key!, region.Id, ct).ConfigureAwait(false);
            return CommandResult.Ok(FormatRank(r));
        }
        catch (NexonApiException ex)
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

    // ---- helpers -----------------------------------------------------------------

    private CommandResult Unknown(string name)
    {
        string known = string.Join(", ", _commands.Select(c => Prefix + c.Name));
        string head = string.IsNullOrEmpty(name) ? "Type a command after '/'." : $"Unknown command \"/{name}\".";
        return CommandResult.Error($"{head} Try: {known}");
    }

    private static string FormatRank(NexonMapleApi.CharacterRank r)
    {
        var lines = new List<string>
        {
            $"{r.Name} · Lv.{r.Level} ({r.ExpRate}%)",
            $"{r.Class} · {r.World}",
        };
        if (r.Guild is not null) lines.Add($"Guild: {r.Guild}");
        if (r.UnionLevel is int ul)
            lines.Add($"Union Lv.{ul}" + (r.UnionGrade is null ? "" : $" · {r.UnionGrade}"));
        if (r.Popularity is int pop) lines.Add($"Popularity {pop}");
        return string.Join("\n", lines);
    }

    private static string FormatGmsRank(NexonGmsRankApi.GmsRank g, NexonGmsRankApi.Server server)
    {
        string total = g.Total is long t ? $" of {t:N0}" : "";
        // Rank is the command's headline value and always present in a real response; if it's somehow
        // missing (degenerate API row), show the world without a bogus "#0" rather than a wrong-looking number.
        string rankLine = g.Rank > 0
            ? $"GMS {server.Tag} Rank #{g.Rank:N0}{total} · {g.World}"
            : $"GMS {server.Tag} · {g.World}";
        var lines = new List<string>
        {
            $"{g.Name} · Lv.{g.Level} {g.Job}",
            rankLine,
        };
        if (g.LegionLevel > 0) lines.Add($"Legion Lv.{g.LegionLevel}");
        return string.Join("\n", lines);
    }

    private sealed record Command(
        string Name, string Usage, string Help, Func<string, CancellationToken, Task<CommandResult>> Run);
}
