using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>
/// Exposes the <c>/remind</c> capability to the chatbot as tools, so natural language — "notify me in 10
/// minutes", "remind me every day at 9am to do dailies", "what reminders do I have?" — schedules/inspects a
/// real reminder. Each tool builds the equivalent <c>/remind …</c> command string and runs it through
/// <see cref="ChatCommands"/> (reusing ALL the time/recurrence parsing, validation and the shared scheduler,
/// so chat-set and slash-set reminders are one list), then hands the confirmation/error text back to the
/// model to phrase a natural reply. Wired into <see cref="PetChatAgent"/> alongside the pet/web/knowledge
/// tools.
/// </summary>
public sealed class ReminderChatTools
{
    private readonly Func<string, CancellationToken, Task<CommandResult>> _runCommand;

    /// <param name="runCommand">Runs a slash command (the app passes <see cref="ChatCommands.RunAsync"/>).
    /// This class only ever builds <c>/remind …</c> strings, so it can't invoke other commands.</param>
    public ReminderChatTools(Func<string, CancellationToken, Task<CommandResult>> runCommand)
        => _runCommand = runCommand;

    public bool Handles(string name) => name is "set_reminder" or "list_reminders" or "cancel_reminder";

    public IReadOnlyList<ChatToolDef> BuildTools() => new[]
    {
        new ChatToolDef("set_reminder",
            "Schedule a reminder the pet will speak to the user later. Use this whenever the user asks to be reminded or notified of something — after a delay, at a clock time, or on a repeating schedule.",
            new Dictionary<string, JsonElement>
            {
                ["time"] = ToolSchema.String("When to fire: a compact duration like 10m, 1h30m, 90s or 1.5h, OR a clock time like 14:23, 9:00 or 1:30pm. For a repeating reminder this is the time of day. Write it compactly, e.g. 10m (not '10 minutes')."),
                ["message"] = ToolSchema.String("What to remind the user about, in their own words (e.g. 'do dailies')."),
                ["repeat"] = ToolSchema.StringEnum("How often to repeat. Omit or 'none' for a one-time reminder.", new[] { "none", "daily", "weekly", "monthly" }),
                ["weekday"] = ToolSchema.StringEnum("For repeat=weekly only: which weekday. Optional — defaults to today's weekday.", new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }),
                ["day_of_month"] = ToolSchema.Number("For repeat=monthly only: the day of the month (1-31). Optional — defaults to today's day."),
            },
            new[] { "time", "message" }),

        new ChatToolDef("list_reminders",
            "List the user's currently scheduled reminders with their ids, so you can tell them what's set or find an id to cancel.",
            new Dictionary<string, JsonElement>(), Array.Empty<string>()),

        new ChatToolDef("cancel_reminder",
            "Cancel a scheduled reminder by its id (from list_reminders), or pass 'all' to cancel every reminder.",
            new Dictionary<string, JsonElement>
            {
                ["id"] = ToolSchema.String("The reminder id to cancel (a number from list_reminders), or 'all' for every reminder."),
            },
            new[] { "id" }),
    };

    /// <summary>Run a reminder tool call: build the <c>/remind …</c> command and return its result text for
    /// the model. Never throws (other than cancellation) — failures come back as text the model can relay.</summary>
    public async Task<string> DispatchAsync(ChatToolCall call, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var a = doc.RootElement;

            string cmd = call.Name switch
            {
                "set_reminder" => BuildSet(a),
                "list_reminders" => "/remind list",
                "cancel_reminder" => $"/remind cancel {(GetString(a, "id") ?? "").Trim()}",
                _ => "",
            };
            if (cmd.Length == 0) return $"Unknown reminder tool '{call.Name}'.";

            var result = await _runCommand(cmd, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(result.Text) ? "(done)" : result.Text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Couldn't update the reminder: {ex.Message}"; }
    }

    private static string BuildSet(JsonElement a)
    {
        // A time spec is always a single token. Drop a leading "in"/"at" and collapse whitespace so common
        // model phrasings still parse: "in 10 minutes" -> "10minutes", "2:00 PM" -> "2:00PM", "1h 30m" -> "1h30m".
        string time = (GetString(a, "time") ?? "").Trim();
        time = Regex.Replace(time, @"^(in|at)\s+", "", RegexOptions.IgnoreCase);
        time = Regex.Replace(time, @"\s+", "");

        string message = (GetString(a, "message") ?? "").Trim();
        string repeat = (GetString(a, "repeat") ?? "none").Trim().ToLowerInvariant();

        string prefix = repeat switch
        {
            "daily" => "-d ",
            "weekly" => "-w " + WithSpace(GetString(a, "weekday")),
            "monthly" => "-m " + WithSpace(GetMonthDay(a)),
            _ => "",
        };
        return $"/remind {prefix}{time} {message}".TrimEnd();
    }

    private static string WithSpace(string? token)
        => string.IsNullOrWhiteSpace(token) ? "" : token.Trim() + " ";

    private static string? GetMonthDay(JsonElement a)
    {
        if (!a.TryGetProperty("day_of_month", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n.ToString();
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s.ToString();
        return null;
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
