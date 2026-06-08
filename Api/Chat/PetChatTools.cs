using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TypePet.Api;

namespace TypePet.Api.Chat;

/// <summary>
/// Bridges the chatbot to the pet's body. The provider-neutral analog of <see cref="Mcp.PetTools"/>:
/// it builds <see cref="ChatToolDef"/>s for a subset of <see cref="IPetControl"/> (with per-character
/// action/expression enums from the capability snapshot) and dispatches a model tool call straight to
/// the in-process control facade — the SAME path the MCP server uses, no network hop. The pet reacts
/// live as the agent runs the call.
/// </summary>
public sealed class PetChatTools
{
    private readonly IPetControl _pet;

    public PetChatTools(IPetControl pet) => _pet = pet;

    private static readonly JsonSerializerOptions ResultJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public bool Handles(string name) => name is
        "say" or "set_expression" or "clear_expression" or "do_action" or
        "walk_to" or "move_to" or "face" or "stop_action";

    /// <summary>Build the pet tools available for the current character (enums reflect what it supports).</summary>
    public IReadOnlyList<ChatToolDef> BuildTools(CapabilitiesSnapshot caps)
    {
        var tools = new List<ChatToolDef>
        {
            // Note: there is no "say" tool — the app speaks the assistant's final reply automatically, so
            // the model only needs movement/expression/action tools to emote.
            new("face", "Make the pet face left or right.",
                new Dictionary<string, JsonElement> { ["direction"] = ToolSchema.StringEnum("Direction to face.", new[] { "left", "right" }) },
                new[] { "direction" }),

            new("walk_to", "Walk the pet to a horizontal x position (logical px) at its current height.",
                new Dictionary<string, JsonElement> { ["x"] = ToolSchema.Number("Target x in logical pixels.") },
                new[] { "x" }),

            new("move_to", "Pathfind the pet to a screen point (logical px).",
                new Dictionary<string, JsonElement>
                {
                    ["x"] = ToolSchema.Number("Target x in logical pixels."),
                    ["y"] = ToolSchema.Number("Target y in logical pixels."),
                },
                new[] { "x", "y" }),

            new("stop_action", "Stop any held action and return the pet to idle.",
                new Dictionary<string, JsonElement>(), Array.Empty<string>()),
        };

        if (caps.Actions.Count > 0)
        {
            var names = caps.Actions.Select(a => a.Name).ToArray();
            tools.Add(new("do_action",
                "Play an action animation so the pet reacts physically (e.g. " + string.Join(", ", names.Take(6)) + ").",
                new Dictionary<string, JsonElement>
                {
                    ["action"] = ToolSchema.StringEnum("Action to play.", names),
                    ["mode"] = ToolSchema.StringEnum("'once' or 'hold'; omit for the action's default.", new[] { "once", "hold" }),
                },
                new[] { "action" }));
        }

        if (caps.Expressions.Count > 0)
        {
            var names = caps.Expressions.Select(e => e.Name).ToArray();
            tools.Add(new("set_expression",
                "Set the pet's facial expression to convey emotion (e.g. " + string.Join(", ", names.Take(6)) + ").",
                new Dictionary<string, JsonElement>
                {
                    ["name"] = ToolSchema.StringEnum("Expression name.", names),
                    ["seconds"] = ToolSchema.Number("Optional: auto-clear after this many seconds."),
                },
                new[] { "name" }));
            tools.Add(new("clear_expression", "Restore the pet's neutral face.",
                new Dictionary<string, JsonElement>(), Array.Empty<string>()));
        }

        return tools;
    }

    /// <summary>Execute a pet tool call against the control facade; return its result as JSON for the model.</summary>
    public async Task<string> DispatchAsync(ChatToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var a = doc.RootElement;

            ControlResult r = call.Name switch
            {
                "say" => await _pet.Say(GetString(a, "text") ?? "", GetNumber(a, "seconds")),
                "set_expression" => await _pet.Expression(GetString(a, "name") ?? "", GetNumber(a, "seconds")),
                "clear_expression" => await _pet.ClearExpression(),
                "do_action" => await _pet.DoAction(GetString(a, "action") ?? "", GetString(a, "mode")),
                "walk_to" => await _pet.WalkTo(GetNumber(a, "x") ?? 0),
                "move_to" => await _pet.MoveTo(GetNumber(a, "x") ?? 0, GetNumber(a, "y") ?? 0),
                "face" => await _pet.Face(GetString(a, "direction") ?? ""),
                "stop_action" => await _pet.StopAction(),
                _ => ControlResult.Fail($"unknown tool '{call.Name}'"),
            };

            return JsonSerializer.Serialize(
                new { status = r.Status.ToString(), reason = r.Reason, detail = r.Detail }, ResultJson);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "Error", reason = ex.Message }, ResultJson);
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
