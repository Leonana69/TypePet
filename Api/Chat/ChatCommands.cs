using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaplePet.Api;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

/// <summary>The outcome of a slash command, displayed exactly like a chat reply: <see cref="Text"/> is
/// spoken by the pet (and shown in history when it's open), <see cref="Sources"/> are optional links shown
/// in history, and <see cref="IsError"/> tints the history bubble. <see cref="Link"/> is a single primary
/// link surfaced as a clickable button IN THE PET'S SPEECH BUBBLE too (the say bar passes it to Say); it's
/// also typically included in <see cref="Sources"/> so it appears in history. <see cref="ImageUrl"/> is an
/// optional image (e.g. the character canvas) shown in both the pet's bubble and the history card.
/// <see cref="ClipboardText"/>, when set, is written to the system clipboard by the say bar (which owns a
/// <c>TopLevel</c>); the command stays UI-free and only carries the text to copy. <see cref="HoldSeconds"/>
/// overrides how long the pet holds the result bubble (and stays still) — null uses the say bar's default.
/// <see cref="ClearHistory"/>, when true (<c>/clear</c>), tells the say bar to wipe the conversation — both
/// the visible history bubbles and the chat agent's context — after speaking this result; like the clipboard
/// write, the command only carries the intent because the say bar owns the history and the agent.</summary>
public sealed record CommandResult(
    string Text, IReadOnlyList<WebSource> Sources, bool IsError, WebSource? Link = null, string? ImageUrl = null,
    string? ClipboardText = null, double? HoldSeconds = null, bool ClearHistory = false)
{
    public static CommandResult Ok(string text, IReadOnlyList<WebSource>? sources = null,
        WebSource? link = null, string? imageUrl = null, string? clipboardText = null, double? holdSeconds = null,
        bool clearHistory = false)
        => new(text, sources ?? Array.Empty<WebSource>(), false, link, imageUrl, clipboardText, holdSeconds, clearHistory);

    public static CommandResult Error(string text) => new(text, Array.Empty<WebSource>(), true);
}

/// <summary>
/// Slash-command handling for the input bar. A message that begins with '/' is parsed into a command name
/// + argument string and dispatched here; the <see cref="CommandResult"/> is shown the same way as a chat
/// reply (the pet speaks it; it's added to history if the panel is open). Most commands are deterministic
/// and run <b>without the LLM</b>, so they work even when the chatbot is off.
///
/// The registry merges a few <b>built-in</b> commands kept in code with the user's <b>command library</b>
/// — declarative <c>command.md</c> files loaded via <see cref="CommandStore"/> + <see cref="CommandInterpreter"/>,
/// hot-reloaded and managed in the Commands tab. The built-ins: <c>/rank [-na|-eu|-kr|-sea|-tw]
/// &lt;character&gt;</c> — a keyless MapleStory lookup whose server is picked per call by a leading flag
/// (<see cref="RankServers"/>: GMS via <see cref="NexonGmsRankApi"/>, KMS/MSEA via
/// <see cref="MapleGgScraper"/>, TMS via <see cref="MapleKitApi"/>); <c>/clear</c> (wipe the chat); and
/// <c>/help</c>. Built-ins win on a name collision, so an uploaded command can't shadow them. (<c>/fortune</c>,
/// the LLM-backed oracle, now ships as a bundled <c>kind:prompt</c> command — see <see cref="CommandInterpreter"/>.)
/// </summary>
public sealed class ChatCommands
{
    /// <summary>The leading character that marks a message as a command.</summary>
    public const char Prefix = '/';

    private readonly NexonGmsRankApi _gms = new();
    private readonly MapleGgScraper _mapleGg = new();
    private readonly MapleKitApi _mapleKit = new();
    private readonly ReminderScheduler _reminders;        // pending /remind reminders, spoken when due

    // The built-in commands kept in code (rank/clear/help) — too complex or state-coupled to express
    // declaratively. ssc/asc/esfera/fortune live as bundled declarative command files instead.
    private readonly Command[] _builtIns;
    private readonly CommandStore? _store;                 // the user command library (null = built-ins only)
    private readonly CommandInterpreter _interp;           // turns a manifest into a runnable command
    private readonly Func<IReadOnlyCollection<string>> _disabledIds; // commands the user turned off (by store id)
    // An immutable snapshot of the merged registry, swapped atomically by Rebuild so an in-flight RunAsync
    // keeps a consistent view without locking. volatile = readers always see the latest publish.
    private volatile Resolved _resolved;

    private sealed record Resolved(IReadOnlyList<Command> Commands, IReadOnlyList<CommandInfo> Info);

    /// <summary>Raised after <see cref="Rebuild"/> swaps in a new registry (a command was added/edited/removed
    /// on disk, or enabled/disabled). The say bar uses it to refresh an open '/' dropdown.</summary>
    public event Action? CommandsChanged;

    /// <summary>UI-facing metadata for every registered command (name, usage, help) — built-ins plus the
    /// enabled user commands. Used by the say bar to populate its '/' dropdown; reflects hot-reloads.</summary>
    public IReadOnlyList<CommandInfo> Commands => _resolved.Info;

    /// <param name="chatEnabled">Whether the chatbot is turned on (Settings → Chatbot). Forwarded to the
    /// interpreter so <c>kind:prompt</c> commands (e.g. the bundled /fortune) can gate on it.</param>
    /// <param name="buildConfig">Resolves the active provider into a ready chat session (backend + model +
    /// key), or null when nothing usable is configured — used by <c>kind:prompt</c> commands.</param>
    /// <param name="pet">The live pet control, or null when not ready — used by prompt/pet/script commands
    /// to read expressions and make the pet emote.</param>
    /// <param name="store">The user command library, or null to expose only the built-ins.</param>
    /// <param name="disabledIds">The store ids the user has turned off (excluded from the registry).</param>
    /// <param name="scriptsEnabled">Whether <c>kind:script</c> commands may run (Settings gate).</param>
    /// <param name="networkApproved">Probe — given a command's store id and its declared <c>hosts:</c> — for
    /// whether the user has approved that command's network access (Commands tab). Null = never approved.</param>
    /// <param name="onReminderChanged">Invoked whenever the reminder list changes (added/cancelled/cleared,
    /// or a reminder fired and re-armed or completed) so the owner can persist it to settings. Null = no
    /// persistence (reminders stay in-memory, as in the headless test harness).</param>
    public ChatCommands(Func<bool> chatEnabled, Func<ChatSessionConfig?> buildConfig, Func<IPetControl?> pet,
        CommandStore? store = null, Func<IReadOnlyCollection<string>>? disabledIds = null,
        Func<bool>? scriptsEnabled = null, Func<string, IReadOnlyCollection<string>, bool>? networkApproved = null,
        Action? onReminderChanged = null)
    {
        _store = store;
        _disabledIds = disabledIds ?? (() => Array.Empty<string>());
        _interp = new CommandInterpreter(chatEnabled, buildConfig, pet, scriptsEnabled ?? (() => true), networkApproved);
        _reminders = new ReminderScheduler(pet, onReminderChanged);
        _builtIns = new[]
        {
            new Command("rank", $"/rank [{RankServers.FlagList.Replace(", ", "|")}] <character>",
                "Look up a MapleStory character by server: -na/-eu = GMS (default -na, with a global rank), -kr = KMS, -sea = MSEA, -tw = TMS.", RankAsync),
            new Command("remind", "/remind <time|-d|-w|-m> <message>",
                "Have the pet remind you. One-off: a duration (10m, 1h30m, 90s, 1.5h, or a plain number = minutes) or a clock time (14:23, 1:30pm). Recurring (clock time required): -d daily, -w [weekday] weekly, -m [day] monthly — e.g. /remind -d 9:00 …, /remind -w mon 20:00 …, /remind -m 1 9:00 …. Also: /remind list, /remind cancel <id>, /remind clear.", RemindAsync),
            new Command("clear", "/clear", "Clear the chat history and start a fresh conversation.", ClearAsync),
            new Command("help", "/help", "List the available commands.", HelpAsync),
        };
        _resolved = new Resolved(_builtIns, Array.Empty<CommandInfo>());
        Rebuild();
    }

    /// <summary>Rebuild the merged registry from the built-ins plus the enabled user commands. Called once
    /// at construction and again whenever the command library changes on disk (the watcher) or a command is
    /// enabled/disabled. Built-ins win on a name collision (an uploaded folder can't shadow <c>/clear</c>);
    /// among user commands, the first to claim a name wins and later duplicates are skipped. Invalid or
    /// unreadable manifests are skipped. Cheap (file reads only); image decode is deferred to display.</summary>
    public void Rebuild()
    {
        var list = new List<Command>(_builtIns);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in _builtIns) taken.Add(b.Name);

        if (_store is not null)
        {
            var disabled = new HashSet<string>(_disabledIds(), StringComparer.OrdinalIgnoreCase);
            foreach (var e in _store.List())
            {
                if (!e.Valid || disabled.Contains(e.Id)) continue;
                var m = _store.ReadManifest(e.Id);
                if (m is null || m.Validate() is not null) continue;
                var run = _interp.Build(m, e.Directory);
                foreach (var nm in m.AllNames())
                    if (taken.Add(nm))
                        list.Add(new Command(nm, m.Usage ?? ("/" + nm), m.Help ?? "", run));
            }
        }

        var info = list.Select(c => new CommandInfo(c.Name, c.Usage, c.Help)).ToArray();
        _resolved = new Resolved(list, info);
        CommandsChanged?.Invoke();
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

        var cmd = _resolved.Commands.FirstOrDefault(c => c.Name == name);
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

    /// <summary><c>/remind &lt;time&gt; &lt;message&gt;</c> — schedule a message the pet speaks later. With no
    /// flag the first token is a one-off time spec (a duration like <c>10m</c>/<c>1h30m</c>/<c>90s</c> or a
    /// clock time like <c>14:23</c>/<c>1:30pm</c>, optionally preceded by <c>in</c>/<c>at</c>). A leading
    /// <c>-d</c>/<c>-w</c>/<c>-m</c> flag makes it recurring (daily/weekly/monthly at a clock time — see
    /// <see cref="RemindRecurring"/>). The management forms <c>/remind list</c>, <c>/remind cancel &lt;id&gt;</c>
    /// and <c>/remind clear</c> inspect and drop pending reminders. Returns immediately with a confirmation;
    /// the reminder itself fires asynchronously via <see cref="ReminderScheduler"/>.</summary>
    private Task<CommandResult> RemindAsync(string args, CancellationToken ct)
    {
        string s = (args ?? "").Trim();
        if (s.Length == 0)
            return Task.FromResult(CommandResult.Error(
                "Usage: /remind <time> <message> — e.g. /remind 10m do dailies, /remind 5 quick check " +
                "(a plain number = minutes), or /remind 14:23 raid. Recurring: /remind -d 9:00 …, " +
                "/remind -w mon 20:00 …, /remind -m 1 9:00 …. Also /remind list, /remind cancel <id>."));

        var (first, rest) = SplitFirstToken(s);

        // Management subcommands (these never collide with a time token: a duration/clock time is never a word).
        switch (first.ToLowerInvariant())
        {
            case "list":
            case "ls":
                return Task.FromResult(ListReminders());
            case "cancel":
            case "remove":
            case "rm":
            case "del":
            case "delete":
                return Task.FromResult(CancelReminder(rest));
            case "clear":
            case "clearall":
                return Task.FromResult(ClearReminders());
        }

        // A leading flag (-d/-w/-m) marks a recurring reminder. A time token never starts with '-', so this
        // is unambiguous (mirrors how /rank picks its server by a leading flag).
        if (first.StartsWith('-'))
            return Task.FromResult(RemindRecurring(first, rest));

        // Allow a natural lead-in: "/remind in 10m …" or "/remind at 14:23 …".
        if ((first.Equals("in", StringComparison.OrdinalIgnoreCase) ||
             first.Equals("at", StringComparison.OrdinalIgnoreCase)) && rest.Length > 0)
            (first, rest) = SplitFirstToken(rest);

        if (!ReminderTimeParser.TryParse(first, DateTime.Now, out var when, out var err))
            return Task.FromResult(CommandResult.Error(
                $"I couldn't read the time \"{first}\" — {err}. Try a duration like 10m, 1h30m or 90s, " +
                "or a clock time like 14:23 or 1:30pm."));

        if (rest.Length == 0)
            return Task.FromResult(CommandResult.Error(
                $"What should I remind you about {when.Display}? Usage: /remind {first} <message>."));

        int id = _reminders.Schedule(when.DueAt, when.Delay, rest);
        return Task.FromResult(CommandResult.Ok($"⏰ Okay! I'll remind you {when.Display}: \"{rest}\"  (#{id})"));
    }

    /// <summary>Map a recurrence flag to a new reminder: parse the body after <c>-d</c>/<c>-w</c>/<c>-m</c>,
    /// schedule it, and confirm. Recurring reminders require an absolute clock time (enforced by the parser).</summary>
    private CommandResult RemindRecurring(string flagToken, string body)
    {
        string flag = flagToken.TrimStart('-').ToLowerInvariant();
        ReminderKind? kind = flag switch
        {
            "d" or "daily" => ReminderKind.Daily,
            "w" or "weekly" => ReminderKind.Weekly,
            "m" or "monthly" => ReminderKind.Monthly,
            _ => null,
        };
        if (kind is null)
            return CommandResult.Error($"Unknown option \"{flagToken}\". Use -d (daily), -w (weekly), or -m (monthly).");

        if (!ReminderTimeParser.TryParseRecurring(kind.Value, body, DateTime.Now, out var rec, out var err))
            return CommandResult.Error(err ?? "I couldn't read that reminder.");

        if (rec.Message.Length == 0)
            return CommandResult.Error(
                $"What should I remind you about — {RecurrenceDescription(rec)}? Add a message after the time.");

        int id = _reminders.ScheduleRecord(rec);
        return CommandResult.Ok($"⏰ Okay! I'll remind you {RecurrenceDescription(rec)}: \"{rec.Message}\"  (#{id})");
    }

    private CommandResult ListReminders()
    {
        var items = _reminders.List();
        if (items.Count == 0)
            return CommandResult.Ok("No reminders set. Add one with /remind <time> <message> (or -d/-w/-m to repeat).");

        var now = DateTime.Now;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var lines = items.Select(it =>
        {
            string? next = it.DueAt > now ? ReminderTimeParser.Humanize(it.DueAt - now) : null;
            string schedule, suffix;
            switch (it.Kind)
            {
                case ReminderKind.Daily:
                    schedule = $"daily {ClockText(it.TimeOfDay)}";
                    suffix = next is null ? "" : $" (next in {next})"; break;
                case ReminderKind.Weekly:
                    schedule = $"weekly {it.Weekday.ToString()[..3]} {ClockText(it.TimeOfDay)}";
                    suffix = next is null ? "" : $" (next in {next})"; break;
                case ReminderKind.Monthly:
                    schedule = $"monthly day {it.DayOfMonth} {ClockText(it.TimeOfDay)}";
                    suffix = next is null ? "" : $" (next in {next})"; break;
                default: // Once
                    schedule = it.DueAt.ToString("h:mm tt", inv) + (it.DueAt.Date != now.Date ? $" ({it.DueAt:MMM d})" : "");
                    suffix = next is null ? "" : $" (in {next})"; break;
            }
            return $"#{it.Id} · {schedule}{suffix} · {Truncate(it.Message, 48)}";
        });
        return CommandResult.Ok($"⏰ Reminders ({items.Count}):\n" + string.Join("\n", lines));
    }

    /// <summary>A human phrase for a recurring reminder's schedule, used in confirmations
    /// ("every day at 2:23 PM", "every Monday at 8:00 PM", "on day 1 of each month at 9:00 AM").</summary>
    private static string RecurrenceDescription(ReminderRecord rec) => rec.Kind switch
    {
        ReminderKind.Daily => $"every day at {ClockText(rec.TimeOfDay)}",
        ReminderKind.Weekly => $"every {rec.Weekday} at {ClockText(rec.TimeOfDay)}",
        ReminderKind.Monthly => $"on day {rec.DayOfMonth} of each month at {ClockText(rec.TimeOfDay)}",
        _ => $"at {ClockText(rec.TimeOfDay)}",
    };

    /// <summary>Format a time-of-day as a 12-hour clock ("2:23 PM", or "1:32:30 PM" when seconds matter),
    /// matching the absolute-time formatting used elsewhere.</summary>
    private static string ClockText(TimeSpan tod)
    {
        var dt = DateTime.MinValue + tod;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return tod.Seconds != 0 ? dt.ToString("h:mm:ss tt", inv) : dt.ToString("h:mm tt", inv);
    }

    /// <summary>A copy of the pending reminders, for the owner to persist (App → settings.json).</summary>
    public IReadOnlyList<ReminderRecord> ReminderSnapshot() => _reminders.Snapshot();

    /// <summary>Rehydrate persisted reminders at startup (recurring recompute their next fire; one-offs whose
    /// time already passed are dropped). Triggers one persist via the onChanged callback.</summary>
    public void LoadReminders(IEnumerable<ReminderRecord> records) => _reminders.Load(records);

    private CommandResult CancelReminder(string arg)
    {
        string a = arg.Trim();
        if (a.Equals("all", StringComparison.OrdinalIgnoreCase)) return ClearReminders();

        if (!int.TryParse(a.TrimStart('#'), out int id))
            return CommandResult.Error("Which one? Use /remind cancel <id> (see /remind list), or /remind cancel all.");
        return _reminders.Cancel(id)
            ? CommandResult.Ok($"🗑️ Cancelled reminder #{id}.")
            : CommandResult.Error($"No pending reminder #{id}. See /remind list.");
    }

    private CommandResult ClearReminders()
    {
        int n = _reminders.Clear();
        return CommandResult.Ok(n == 0 ? "No reminders to clear." : $"🗑️ Cleared {n} reminder{(n == 1 ? "" : "s")}.");
    }

    /// <summary>Split the first whitespace-delimited token off <paramref name="s"/> (already trimmed),
    /// returning it plus the trimmed remainder ("" when there's no remainder).</summary>
    private static (string first, string rest) SplitFirstToken(string s)
    {
        int sp = s.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private Task<CommandResult> HelpAsync(string args, CancellationToken ct)
    {
        string list = string.Join("\n", _resolved.Commands.Select(c => $"{c.Usage} — {c.Help}"));
        return Task.FromResult(CommandResult.Ok("Commands:\n" + list));
    }

    /// <summary><c>/clear</c> — start a fresh conversation. The command itself is pure: it just sets
    /// <see cref="CommandResult.ClearHistory"/>; the say bar does the actual wipe (visible bubbles + the chat
    /// agent's context), since it owns both. Works whether or not the chatbot is on.</summary>
    private static Task<CommandResult> ClearAsync(string args, CancellationToken ct)
        => Task.FromResult(CommandResult.Ok("🧹 Chat history cleared.", clearHistory: true));

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
        string known = string.Join(", ", _resolved.Commands.Select(c => Prefix + c.Name));
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
