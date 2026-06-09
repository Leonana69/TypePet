using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OAI = OpenAI.Chat;

namespace TypePet.Api.Chat;

/// <summary>
/// <see cref="IChatBackend"/> for the OpenAI Chat Completions wire format via the official <c>OpenAI</c>
/// SDK. Because the SDK's <see cref="OAI.ChatClient"/> accepts a custom <see cref="OpenAIClientOptions.Endpoint"/>,
/// this one backend also drives DeepSeek and local servers (Ollama, LM Studio, vLLM) — they speak the
/// same format; only the base URL, model, and key differ. Non-streaming: one completion per turn.
/// </summary>
public sealed class OpenAiCompatibleBackend : IChatBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly OAI.ChatClient _client;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    /// <param name="baseUrl">Provider base URL (e.g. https://api.openai.com/v1, https://api.deepseek.com,
    /// http://localhost:11434/v1). Null/empty → the SDK's default OpenAI endpoint.</param>
    /// <param name="apiKey">API key; may be empty for keyless local servers (a placeholder is sent).</param>
    public OpenAiCompatibleBackend(string baseUrl, string apiKey, string model)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        _apiKey = apiKey ?? "";
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            options.Endpoint = uri;
        var key = string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey; // local servers ignore it; SDK needs non-empty
        var m = string.IsNullOrWhiteSpace(model) ? "model" : model;  // ChatClient requires a non-empty model id
        _client = new OAI.ChatClient(m, new ApiKeyCredential(key), options);
    }

    public bool SupportsTools => true;

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return ModelIds.Parse(await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false));
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<ChatTurn> SendAsync(ChatRequest request, CancellationToken ct)
    {
        var messages = new List<OAI.ChatMessage> { new OAI.SystemChatMessage(request.System) };
        foreach (var m in request.Messages)
            messages.Add(ToOpenAi(m));

        var options = new OAI.ChatCompletionOptions { MaxOutputTokenCount = request.MaxTokens };
        foreach (var t in request.Tools)
            options.Tools.Add(OAI.ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.SchemaJson())));

        OAI.ChatCompletion completion = await _client.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);

        var text = new StringBuilder();
        foreach (var part in completion.Content)
            if (part.Kind == OAI.ChatMessageContentPartKind.Text)
                text.Append(part.Text);

        var calls = new List<ChatToolCall>();
        foreach (var c in completion.ToolCalls)
            calls.Add(new ChatToolCall(c.Id, c.FunctionName, c.FunctionArguments.ToString()));

        return new ChatTurn(text.ToString(), calls);
    }

    private static OAI.ChatMessage ToOpenAi(ChatMessage m)
    {
        switch (m.Role)
        {
            case ChatRole.User:
                return new OAI.UserChatMessage(m.Text ?? "");

            case ChatRole.Tool:
                return new OAI.ToolChatMessage(m.ToolCallId ?? "", m.Text ?? "");

            case ChatRole.Assistant:
                if (m.ToolCalls is { Count: > 0 } calls)
                {
                    var toolCalls = new List<OAI.ChatToolCall>();
                    foreach (var c in calls)
                        toolCalls.Add(OAI.ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.ArgumentsJson)));
                    var asst = new OAI.AssistantChatMessage(toolCalls);
                    if (!string.IsNullOrEmpty(m.Text))
                        asst.Content.Add(OAI.ChatMessageContentPart.CreateTextPart(m.Text));
                    return asst;
                }
                return new OAI.AssistantChatMessage(m.Text ?? "");

            default:
                return new OAI.UserChatMessage(m.Text ?? "");
        }
    }
}
