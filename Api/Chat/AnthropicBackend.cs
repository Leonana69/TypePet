using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;

namespace TypePet.Api.Chat;

/// <summary>
/// <see cref="IChatBackend"/> for Claude via the official <c>Anthropic</c> SDK. Non-streaming: one
/// <c>Messages.Create</c> per turn (the agent runs the tool loop). Translates the neutral conversation
/// into the Messages API shape — assistant <c>tool_use</c> blocks, and tool results folded into a single
/// following user turn of <c>tool_result</c> blocks, as the API requires.
/// </summary>
public sealed class AnthropicBackend : IChatBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AnthropicClient _client;
    private readonly string _apiKey;

    public AnthropicBackend(string apiKey)
    {
        _apiKey = apiKey;
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    public bool SupportsTools => true;

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return ModelIds.Parse(await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false));
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<ChatTurn> SendAsync(ChatRequest request, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = ToParams(request.Messages),
            Tools = request.Tools.Count > 0 ? ToTools(request.Tools) : null,
        };

        Message resp = await _client.Messages.Create(p, cancellationToken: ct).ConfigureAwait(false);

        var text = new System.Text.StringBuilder();
        var calls = new List<ChatToolCall>();
        foreach (var block in resp.Content)
        {
            if (block.TryPickText(out TextBlock? tb) && tb is not null)
                text.Append(tb.Text);
            else if (block.TryPickToolUse(out ToolUseBlock? tu) && tu is not null)
                calls.Add(new ChatToolCall(tu.ID, tu.Name, JsonSerializer.Serialize(tu.Input)));
        }
        return new ChatTurn(text.ToString(), calls);
    }

    // ---- neutral -> Messages API -------------------------------------------------

    private static List<MessageParam> ToParams(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<MessageParam>();
        // Consecutive user-side messages (tool results, then perhaps an agent nudge or back-to-back user
        // texts) fold into ONE user turn of blocks — the API requires tool_result blocks in a user message
        // and rejects empty content, and this keeps roles cleanly alternating.
        var pendingUserBlocks = new List<ContentBlockParam>();

        void Flush()
        {
            if (pendingUserBlocks.Count == 0) return;
            result.Add(new MessageParam { Role = Role.User, Content = new List<ContentBlockParam>(pendingUserBlocks) });
            pendingUserBlocks.Clear();
        }

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool)
            {
                pendingUserBlocks.Add(new ToolResultBlockParam
                {
                    ToolUseID = m.ToolCallId ?? "",
                    Content = m.Text ?? "",
                    IsError = m.IsError,
                });
            }
            else if (m.Role == ChatRole.User)
            {
                pendingUserBlocks.Add(new TextBlockParam { Text = m.Text ?? "" });
            }
            else if (m.Role == ChatRole.Assistant)
            {
                var blocks = new List<ContentBlockParam>();
                // Whitespace-only counts as empty: the API rejects text blocks with no non-whitespace.
                if (!string.IsNullOrWhiteSpace(m.Text))
                    blocks.Add(new TextBlockParam { Text = m.Text });
                if (m.ToolCalls is { } calls)
                    foreach (var c in calls)
                        blocks.Add(new ToolUseBlockParam { ID = c.Id, Name = c.Name, Input = ParseInput(c.ArgumentsJson) });
                if (blocks.Count == 0) continue; // empty assistant turn — the API rejects empty content
                Flush();
                result.Add(new MessageParam { Role = Role.Assistant, Content = blocks });
            }
        }

        Flush();
        return result;
    }

    private static List<ToolUnion> ToTools(IReadOnlyList<ChatToolDef> tools) =>
        tools.Select(t => (ToolUnion)new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>(t.Properties),
                Required = t.Required.ToList(),
            },
        }).ToList();

    private static Dictionary<string, JsonElement> ParseInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new(); }
        catch { return new(); }
    }
}
