using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaplePet.Api.Chat;

/// <summary>
/// Provider-neutral chat primitives the agent loop (<see cref="PetChatAgent"/>) speaks. Each
/// <see cref="IChatBackend"/> translates these to/from its provider's wire format (Anthropic Messages
/// API or the OpenAI Chat Completions format shared by OpenAI / DeepSeek / Ollama / LM Studio), so the
/// agent, the tool bridge, and the web-search tool are written once and run against every provider.
/// </summary>
public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A model-requested tool invocation. <see cref="ArgumentsJson"/> is the raw JSON object of
/// arguments (parse it; never string-match it).</summary>
public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>One message in the neutral conversation. Assistant turns may carry <see cref="ToolCalls"/>;
/// a <see cref="ChatRole.Tool"/> message carries a tool result (<see cref="Text"/>) answering
/// <see cref="ToolCallId"/>.</summary>
public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public bool IsError { get; init; }

    public static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };
    public static ChatMessage Assistant(string? text, IReadOnlyList<ChatToolCall>? calls) =>
        new() { Role = ChatRole.Assistant, Text = text, ToolCalls = calls };
    public static ChatMessage ToolResult(string toolCallId, string content, bool isError = false) =>
        new() { Role = ChatRole.Tool, Text = content, ToolCallId = toolCallId, IsError = isError };
}

/// <summary>A tool the model may call. <see cref="Properties"/> are JSON-Schema fragments per argument
/// (e.g. <c>{ "type":"string", "description":"..." }</c>); <see cref="Required"/> lists the mandatory
/// ones. Kept in this split form because Anthropic wants <c>properties</c>/<c>required</c> directly while
/// OpenAI wants the whole schema object — <see cref="SchemaJson"/> assembles the latter.</summary>
public sealed record ChatToolDef(
    string Name,
    string Description,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<string> Required)
{
    /// <summary>The full JSON-Schema object for this tool's input (OpenAI-style "parameters").</summary>
    public string SchemaJson()
    {
        var props = new JsonObject();
        foreach (var (k, v) in Properties) props[k] = JsonNode.Parse(v.GetRawText());
        var req = new JsonArray();
        foreach (var r in Required) req.Add(r);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = req,
            ["additionalProperties"] = false,
        };
        return schema.ToJsonString();
    }
}

/// <summary>One request to a backend: the system prompt, the conversation so far, the offered tools,
/// and per-call knobs.</summary>
public sealed record ChatRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatToolDef> Tools,
    string Model,
    int MaxTokens);

/// <summary>The assistant's reply for one turn (non-streaming): its text, any tool calls it wants run,
/// and whether it is done (<see cref="ToolCalls"/> empty) or awaiting tool results.</summary>
public sealed record ChatTurn(string Text, IReadOnlyList<ChatToolCall> ToolCalls);

/// <summary>Parses the shared <c>{ "data": [ { "id": ... } ] }</c> shape returned by both providers'
/// <c>/models</c> endpoints into a sorted id list. Takes ownership of (and disposes) the document.</summary>
public static class ModelIds
{
    public static IReadOnlyList<string> Parse(JsonDocument doc)
    {
        using (doc)
        {
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString()!);
            ids.Sort(System.StringComparer.OrdinalIgnoreCase);
            return ids;
        }
    }
}

/// <summary>Helpers for building <see cref="ChatToolDef.Properties"/> JSON-Schema fragments.</summary>
public static class ToolSchema
{
    public static JsonElement String(string description) =>
        JsonSerializer.SerializeToElement(new { type = "string", description });

    public static JsonElement StringEnum(string description, IEnumerable<string> values) =>
        JsonSerializer.SerializeToElement(new { type = "string", description, @enum = values });

    public static JsonElement Number(string description) =>
        JsonSerializer.SerializeToElement(new { type = "number", description });
}
