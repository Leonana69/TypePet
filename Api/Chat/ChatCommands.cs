using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>The outcome of a slash command, displayed exactly like a chat reply: <see cref="Text"/> is
/// spoken by the pet (and shown in history when it's open), <see cref="Sources"/> are optional links,
/// and <see cref="IsError"/> tints the history bubble.</summary>
public sealed record CommandResult(string Text, IReadOnlyList<WebSource> Sources, bool IsError)
{
    public static CommandResult Ok(string text, IReadOnlyList<WebSource>? sources = null)
        => new(text, sources ?? Array.Empty<WebSource>(), false);

    public static CommandResult Error(string text) => new(text, Array.Empty<WebSource>(), true);
}

/// <summary>
/// Slash-command handling for the input bar. Commands are deterministic and run <b>without the LLM</b>
/// — so they work even when the chatbot is off or unconfigured. A message that begins with '/' is parsed
/// into a command name + argument string and dispatched here; the <see cref="CommandResult"/> is then
/// shown the same way as a chat reply (the pet speaks it; it's added to history if the panel is open).
/// Adding a command is a one-line edit to the table in the constructor.
///
/// First command: <c>/rank &lt;character&gt;</c> — looks a MapleStory (KMS) character up via the Nexon
/// Open API (<see cref="NexonMapleApi"/>).
/// </summary>
public sealed class ChatCommands
{
    /// <summary>The leading character that marks a message as a command.</summary>
    public const char Prefix = '/';

    private readonly Func<string?> _nexonKey;
    private readonly Func<string> _region;
    private readonly NexonMapleApi _maple = new();
    private readonly IReadOnlyList<Command> _commands;

    /// <param name="nexonKey">Live accessor for the Nexon Open API key of the SELECTED region (read per
    /// call, so a key/region edited in Settings applies immediately). Null/empty means "not configured".</param>
    /// <param name="region">Live accessor for the selected MapleStory region id (kms/sea/tms).</param>
    public ChatCommands(Func<string?> nexonKey, Func<string> region)
    {
        _nexonKey = nexonKey;
        _region = region;
        _commands = new[]
        {
            new Command("rank", "/rank <character>", "Look up a MapleStory character (KMS): level, class, world, union.", RankAsync),
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
        string charName = args.Trim();
        if (charName.Length == 0) return CommandResult.Error("Usage: /rank <character name>");

        string region = _region();
        string? key = _nexonKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            string label = NexonMapleApi.ResolveRegion(region).Label;
            return CommandResult.Error($"No {label} Nexon API key set. Add it in Settings → MAPLESTORY to use /rank.");
        }

        try
        {
            var r = await _maple.GetRankAsync(charName, key!, region, ct).ConfigureAwait(false);
            return CommandResult.Ok(FormatRank(r));
        }
        catch (NexonApiException ex)
        {
            return CommandResult.Error(ex.Message); // already user-facing
        }
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

    private sealed record Command(
        string Name, string Usage, string Help, Func<string, CancellationToken, Task<CommandResult>> Run);
}
