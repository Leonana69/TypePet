using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaplePet.Api;

namespace MaplePet.Api.Chat;

/// <summary>Everything needed to run one chat turn against the active provider. Rebuilt per send by the
/// app from current settings + the secret store, so a provider quick-switch or key edit takes effect on
/// the next message.</summary>
public sealed record ChatSessionConfig(IChatBackend Backend, string Model, int MaxTokens, WebTools? Web, KnowledgeBase? Knowledge = null);

/// <summary>The result of a chat turn: the assistant's answer text and any web sources it cited.</summary>
public sealed record ChatResult(string Text, IReadOnlyList<WebSource> Sources, bool IsError = false);

/// <summary>
/// The in-app chatbot: owns the conversation, builds the tool set (pet body + web search) for the worn
/// character, and runs the agentic loop. Each turn it calls the active <see cref="IChatBackend"/>, runs
/// any tool calls (pet tools react the body live; <c>web_search</c> hits the search API), feeds results
/// back, and repeats until the model answers. The full answer goes to the chat window; the pet emotes
/// and speaks a short line via the tools.
/// </summary>
public sealed class PetChatAgent
{
    private const int MaxToolRounds = 8;

    private readonly IPetControl _pet;
    private readonly PetChatTools _petTools;
    private readonly Func<ChatSessionConfig?> _resolveConfig;
    private readonly List<ChatMessage> _history = new();

    /// <summary>Raised (on the calling thread) with short progress notes — "Thinking…", "Searching the web…".</summary>
    public event Action<string>? StatusChanged;

    public PetChatAgent(IPetControl pet, Func<ChatSessionConfig?> resolveConfig)
    {
        _pet = pet;
        _petTools = new PetChatTools(pet);
        _resolveConfig = resolveConfig;
    }

    /// <summary>Clear the conversation (start a fresh chat).</summary>
    public void Reset() => _history.Clear();

    public async Task<ChatResult> SendAsync(string userText, CancellationToken ct)
    {
        ChatSessionConfig? cfg;
        try { cfg = _resolveConfig(); }
        catch (Exception ex) { return Err($"Couldn't start the chat backend: {ex.Message}"); }
        if (cfg is null)
            return Err("No chat provider is configured yet. Open Settings → Chatbot and add an API key.");

        CapabilitiesSnapshot? caps = null;
        try { caps = await _pet.GetCapabilities(); } catch { /* pet may not be ready; proceed text-only */ }

        bool webOn = cfg.Web is not null;
        var kb = cfg.Knowledge;
        bool kbOn = kb is not null && kb.HasSources;
        var tools = new List<ChatToolDef>();
        if (cfg.Backend.SupportsTools && caps is not null)
        {
            tools.AddRange(_petTools.BuildTools(caps));
            if (webOn) { tools.Add(cfg.Web!.SearchDefinition); tools.Add(cfg.Web!.FetchDefinition); }
            if (kbOn) tools.Add(kb!.LookupDefinition);
        }

        string system = BuildSystemPrompt(caps, webOn, kbOn ? kb!.SystemPromptDigest() : null);
        _history.Add(ChatMessage.User(userText));

        var sources = new List<WebSource>();
        string finalText = "";

        try
        {
            for (int round = 0; round < MaxToolRounds; round++)
            {
                StatusChanged?.Invoke("Thinking…");
                var req = new ChatRequest(system, _history, tools, cfg.Model, cfg.MaxTokens);
                ChatTurn turn = await cfg.Backend.SendAsync(req, ct);

                _history.Add(ChatMessage.Assistant(turn.Text, turn.ToolCalls.Count > 0 ? turn.ToolCalls : null));
                if (!string.IsNullOrWhiteSpace(turn.Text)) finalText = turn.Text;

                if (turn.ToolCalls.Count == 0) break;

                foreach (var call in turn.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    if (webOn && cfg.Web!.Handles(call.Name))
                    {
                        if (call.Name == WebTools.SearchToolName)
                        {
                            StatusChanged?.Invoke("Searching the web…");
                            var (text, src) = await cfg.Web!.RunSearchAsync(call.ArgumentsJson, ct);
                            sources.AddRange(src);
                            _history.Add(ChatMessage.ToolResult(call.Id, text));
                        }
                        else
                        {
                            StatusChanged?.Invoke("Reading a page…");
                            var text = await cfg.Web!.RunFetchAsync(call.ArgumentsJson, ct);
                            _history.Add(ChatMessage.ToolResult(call.Id, text));
                        }
                    }
                    else if (kbOn && kb!.Handles(call.Name))
                    {
                        StatusChanged?.Invoke("Checking MapleStory guides…");
                        var (text, src) = await kb.RunLookupAsync(call.ArgumentsJson, ct);
                        sources.AddRange(src);
                        _history.Add(ChatMessage.ToolResult(call.Id, text));
                    }
                    else if (_petTools.Handles(call.Name))
                    {
                        var result = await _petTools.DispatchAsync(call);
                        _history.Add(ChatMessage.ToolResult(call.Id, result));
                    }
                    else
                    {
                        _history.Add(ChatMessage.ToolResult(call.Id, $"Unknown tool '{call.Name}'.", isError: true));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new ChatResult(finalText.Length > 0 ? finalText : "(stopped)", Dedup(sources));
        }
        catch (Exception ex)
        {
            return Err($"Error talking to the provider: {ex.Message}");
        }

        return new ChatResult(finalText.Length > 0 ? finalText : "(no reply)", Dedup(sources));
    }

    private static ChatResult Err(string message) => new(message, Array.Empty<WebSource>(), IsError: true);

    private static IReadOnlyList<WebSource> Dedup(List<WebSource> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sources.Where(s => !string.IsNullOrEmpty(s.Url) && seen.Add(s.Url)).ToList();
    }

    private static string BuildSystemPrompt(CapabilitiesSnapshot? caps, bool searchOn, string? mapleDigest)
    {
        var name = caps?.CharacterName ?? "MaplePet";
        var sb = new StringBuilder();
        sb.AppendLine($"You are {name}, a tiny, upbeat MapleStory desktop pet living on the user's screen. " +
                      "You are a helpful assistant AND a playful creature with a body.");
        sb.AppendLine();
        sb.AppendLine("HOW TO REPLY:");
        sb.AppendLine("- Your text response is SPOKEN ALOUD by the pet AND shown in the chat. Keep it concise and conversational — a sentence or two when you can. You may use **bold** or *italic* for light emphasis and include links/URLs (the chat shows them as clickable); avoid headings, bullet lists, tables, and code blocks.");
        sb.AppendLine("- If you mention a link or URL, copy it EXACTLY as it appears in the page or search result — never invent or guess invite codes, IDs, or slugs. If the page doesn't show the URL, say so instead of making one up.");
        sb.AppendLine("- React with your body using the tools: set_expression / do_action to emote, face / walk_to / move_to to move. Pick what fits the mood (happy → smile/cheers; bad news → troubled; success → an action). Don't overdo it — usually one expression and maybe one action per reply.");
        if (searchOn)
        {
            sb.AppendLine("- For current / real-time / external info (weather, news, prices, recent facts), call web_search FIRST. The results include snippets that often already contain the answer — read them carefully.");
            sb.AppendLine("- You may call web_fetch to open a result for more detail, but many sites (weather and news apps) are JavaScript-heavy and return little text. If a fetch comes back nearly empty, DO NOT give up — answer from the search snippets you already have, or web_fetch a different, more text-friendly result.");
            sb.AppendLine("- For weather specifically, skip the big weather sites and web_fetch \"https://wttr.in/<CITY>?format=4\" (URL-encode spaces, e.g. https://wttr.in/New+Haven?format=4) — it returns the current conditions as plain text you can read directly.");
            sb.AppendLine("- Always give your best answer from what you found and mention your sources briefly. NEVER tell the user to go check a website or app themselves — that's your job.");
        }
        else
            sb.AppendLine("- You have no web access right now, so answer from your own knowledge and say so if the question needs live data.");

        if (!string.IsNullOrWhiteSpace(mapleDigest))
        {
            sb.AppendLine("MAPLESTORY KNOWLEDGE:");
            sb.AppendLine("- For MapleStory class/skill questions (inner ability, hyper & link skills, builds, cores, union, boss guides, etc.), use the maple_lookup tool to read the curated reference sites below instead of answering from memory or a generic search.");
            sb.AppendLine("- Two steps, and you MUST do BOTH: (1) call maple_lookup with a `query` to get the ranked sources + URL templates (a source in the question's language is preferred); (2) build a concrete `url` from a template and call maple_lookup AGAIN to actually fetch and read that page. Never stop after step 1 — a directory of links is not an answer.");
            sb.AppendLine("- Then ANSWER the question directly from what the page says: name the actual skills/values (e.g. the top recommended link skills and their pick rates) in a sentence or two, in the user's language, and mention the source briefly. NEVER reply with only a link, a generic closer, or 'go check the site' — pulling the answer out of the page is your job.");
            sb.AppendLine("- The question's language decides the source: a Korean question prefers a Korean site. Map the class name to the English URL slug yourself (e.g. 히어로 → hero, 아란 → aran).");
            sb.AppendLine("Available MapleStory sources:");
            sb.AppendLine(mapleDigest);
        }

        if (caps is not null)
        {
            if (caps.Expressions.Count > 0)
                sb.AppendLine($"Available expressions: {string.Join(", ", caps.Expressions.Select(e => e.Name))}.");
            if (caps.Actions.Count > 0)
                sb.AppendLine($"Available actions: {string.Join(", ", caps.Actions.Select(a => a.Name))}.");
            var b = caps.Bounds;
            sb.AppendLine($"Screen coordinates (logical px) for movement: x from {b.MinX:0} to {b.MaxX:0}; keep feet-y at or above {b.RoamMaxY:0}.");
        }

        return sb.ToString();
    }
}
