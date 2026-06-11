using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TypePet.Api;

namespace TypePet.Api.Chat;

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
    private readonly ReminderChatTools? _reminderTools;
    private readonly Func<ChatSessionConfig?> _resolveConfig;
    private readonly List<ChatMessage> _history = new();

    /// <summary>Raised (on the calling thread) with short progress notes — "Thinking…", "Searching the web…".</summary>
    public event Action<string>? StatusChanged;

    /// <param name="runReminderCommand">Runs a slash command (the app passes <see cref="ChatCommands.RunAsync"/>),
    /// enabling the <c>set_reminder</c>/<c>list_reminders</c>/<c>cancel_reminder</c> tools so the model can
    /// schedule real reminders from natural language. Null disables those tools.</param>
    public PetChatAgent(IPetControl pet, Func<ChatSessionConfig?> resolveConfig,
        Func<string, CancellationToken, Task<CommandResult>>? runReminderCommand = null)
    {
        _pet = pet;
        _petTools = new PetChatTools(pet);
        _reminderTools = runReminderCommand is null ? null : new ReminderChatTools(runReminderCommand);
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
            if (_reminderTools is not null) tools.AddRange(_reminderTools.BuildTools());
        }

        string system = BuildSystemPrompt(caps, webOn, kbOn ? kb!.SystemPromptDigest() : null, _reminderTools is not null);
        _history.Add(ChatMessage.User(userText));

        var sources = new List<WebSource>();
        string finalText = "";

        // Deterministic backstops for the two ways models bail out of retrieval: stopping after a
        // search/directory without ever opening a page ("here are some links!"), and ending a tool round
        // with no text at all (which would otherwise surface a stale "Let me look that up…" preamble).
        bool anyToolRan = false, sawDirectory = false, fetchedPage = false, searchedWeb = false;
        bool nudgedAnswer = false, nudgedFetch = false;
        string preNudgeAnswer = "";        // the answer nudge (b) asked to improve — restored if nothing better arrives
        bool awaitingNudgeAnswer = false;
        ChatMessage? lastNudge = null;

        try
        {
            for (int round = 0; round < MaxToolRounds; round++)
            {
                StatusChanged?.Invoke("Thinking…");
                var req = new ChatRequest(system, _history, tools, cfg.Model, cfg.MaxTokens);
                ChatTurn turn = await cfg.Backend.SendAsync(req, ct);

                bool hasText = !string.IsNullOrWhiteSpace(turn.Text);
                // Never record an empty assistant turn — Anthropic rejects empty content on the next send.
                if (hasText || turn.ToolCalls.Count > 0)
                    _history.Add(ChatMessage.Assistant(turn.Text, turn.ToolCalls.Count > 0 ? turn.ToolCalls : null));
                if (hasText) finalText = turn.Text;

                if (turn.ToolCalls.Count == 0)
                {
                    if (hasText) awaitingNudgeAnswer = false; // a complete post-nudge answer arrived
                    if (!hasText && anyToolRan && !nudgedAnswer)
                    {
                        nudgedAnswer = true;
                        lastNudge = ChatMessage.User("(Reply to the user now in plain text, based on what you " +
                            "just did or found. If something is still missing, say so — don't reply with just links.)");
                        _history.Add(lastNudge);
                        continue;
                    }
                    // Only when the directory really was the model's last evidence: a model that pivoted to
                    // web_search and answered from snippets is following its instructions, not bailing out.
                    if (hasText && sawDirectory && !fetchedPage && !searchedWeb && !nudgedFetch)
                    {
                        nudgedFetch = true;
                        preNudgeAnswer = finalText;
                        awaitingNudgeAnswer = true;
                        lastNudge = ChatMessage.User("(You stopped after the source directory without reading a " +
                            "page — a link is not an answer. Call maple_lookup again with a concrete `url` built " +
                            "from a template above, keep `query` set to the topic, and answer from what the page " +
                            "says.)");
                        _history.Add(lastNudge);
                        continue;
                    }
                    break;
                }
                anyToolRan = true;

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
                            searchedWeb = true;
                            _history.Add(ChatMessage.ToolResult(call.Id, text));
                        }
                        else
                        {
                            StatusChanged?.Invoke("Reading a page…");
                            var text = await cfg.Web!.RunFetchAsync(call.ArgumentsJson, ct);
                            fetchedPage = true;
                            _history.Add(ChatMessage.ToolResult(call.Id, text));
                        }
                    }
                    else if (kbOn && kb!.Handles(call.Name))
                    {
                        StatusChanged?.Invoke("Checking game guides…");
                        var (text, src) = await kb.RunLookupAsync(call.ArgumentsJson, ct);
                        sources.AddRange(src);
                        // Cited a source ⇒ a page was actually read. No `url` argument ⇒ the directory was
                        // returned. A failed fetch attempt (url given, nothing read) sets neither, so the
                        // fetch nudge stays armed exactly when retrieval still hasn't happened.
                        if (src.Count > 0) fetchedPage = true;
                        else if (!HasArg(call.ArgumentsJson, "url")) sawDirectory = true;
                        _history.Add(ChatMessage.ToolResult(call.Id, text));
                    }
                    else if (_reminderTools is not null && _reminderTools.Handles(call.Name))
                    {
                        StatusChanged?.Invoke("Setting a reminder…");
                        var text = await _reminderTools.DispatchAsync(call, ct);
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
            if (awaitingNudgeAnswer && preNudgeAnswer.Length > 0) finalText = preNudgeAnswer;
            return new ChatResult(finalText.Length > 0 ? finalText : "(stopped)", Dedup(sources));
        }
        catch (Exception ex)
        {
            return Err($"Error talking to the provider: {ex.Message}");
        }
        finally
        {
            // A nudge nobody answered (round budget ran out, cancel, provider error) must not dangle as
            // the last message — the next user question would arrive fused with the stale demand.
            if (lastNudge is not null && _history.Count > 0 && ReferenceEquals(_history[^1], lastNudge))
                _history.RemoveAt(_history.Count - 1);
        }

        // The fetch nudge never produced a finished answer — keep the answer it interrupted rather than
        // whatever preamble the extra rounds left behind.
        if (awaitingNudgeAnswer && preNudgeAnswer.Length > 0) finalText = preNudgeAnswer;
        return new ChatResult(finalText.Length > 0 ? finalText : "(no reply)", Dedup(sources));
    }

    private static ChatResult Err(string message) => new(message, Array.Empty<WebSource>(), IsError: true);

    /// <summary>True if the tool-call arguments JSON has a non-empty string property of this name.</summary>
    private static bool HasArg(string json, string name)
    {
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return d.RootElement.TryGetProperty(name, out var v) &&
                   v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
        }
        catch { return false; }
    }

    private static IReadOnlyList<WebSource> Dedup(List<WebSource> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sources.Where(s => !string.IsNullOrEmpty(s.Url) && seen.Add(s.Url)).ToList();
    }

    private static string BuildSystemPrompt(CapabilitiesSnapshot? caps, bool searchOn, string? mapleDigest, bool remindersOn)
    {
        var name = caps?.CharacterName ?? "TypePet";
        var sb = new StringBuilder();
        sb.AppendLine($"You are {name}, a tiny, upbeat desktop pet living on the user's screen. " +
                      "You are a helpful assistant AND a playful creature with a body.");
        sb.AppendLine();
        sb.AppendLine($"The current date and time (the user's local time) is {PromptTime.Now()}. Use it to " +
                      "resolve relative times like \"today\", \"tonight\", \"in an hour\", or \"tomorrow morning\", " +
                      "and when the user asks what day or time it is.");
        sb.AppendLine();
        sb.AppendLine("HOW TO REPLY:");
        sb.AppendLine("- Your text response is SPOKEN ALOUD by the pet AND shown in the chat. Keep it concise and conversational — a sentence or two when you can. You may use **bold** or *italic* for light emphasis and include links/URLs (the chat shows them as clickable); avoid headings, bullet lists, tables, and code blocks.");
        sb.AppendLine("- If you mention a link or URL, copy it EXACTLY as it appears in the page or search result — never invent or guess invite codes, IDs, or slugs. If the page doesn't show the URL, say so instead of making one up.");
        sb.AppendLine("- React with your body using the tools: set_expression / do_action to emote, face / walk_to / move_to to move. Pick what fits the mood (happy → smile/cheers; bad news → troubled; success → an action). Don't overdo it — usually one expression and maybe one action per reply.");
        if (remindersOn)
            sb.AppendLine("- If the user asks to be reminded or notified of something later (\"remind me in 10 minutes\", \"notify me at 2pm to log off\", \"every day at 9am do dailies\"), call set_reminder with a compact time and their message — set repeat to daily/weekly/monthly for recurring ones. Use list_reminders / cancel_reminder to show or remove reminders. After it succeeds, confirm naturally in your reply (e.g. \"Okay! I'll remind you in 10 minutes 😊\").");
        if (searchOn)
        {
            sb.AppendLine("- For current / real-time / external info (weather, news, prices, recent facts), call web_search FIRST. The results include snippets that often already contain the answer — read them carefully.");
            sb.AppendLine("- A link is NOT an answer. If the snippets don't already answer the question, web_fetch the most promising result and extract the answer from the page — only cite a link AFTER you've read it. Pass `query` keywords to web_fetch on long pages to get the sections you need instead of the page's beginning.");
            sb.AppendLine("- If a fetch comes back nearly empty (some sites are JavaScript-heavy), DO NOT give up — answer from the search snippets you already have, or web_fetch a different, more text-friendly result.");
            sb.AppendLine("- For weather specifically, skip the big weather sites and web_fetch \"https://wttr.in/<CITY>?format=4\" (URL-encode spaces, e.g. https://wttr.in/New+Haven?format=4) — it returns the current conditions as plain text you can read directly.");
            sb.AppendLine("- Always give your best answer from what you found and mention your sources briefly. NEVER tell the user to go check a website or app themselves — that's your job.");
        }
        else
            sb.AppendLine("- You have no web access right now, so answer from your own knowledge and say so if the question needs live data.");

        if (!string.IsNullOrWhiteSpace(mapleDigest))
        {
            sb.AppendLine("GAME KNOWLEDGE:");
            sb.AppendLine("- For game class/skill questions (inner ability, hyper & link skills, builds, cores, union, boss guides, etc.), use the maple_lookup tool to read the curated reference sites below instead of answering from memory or a generic search.");
            sb.AppendLine("- Two steps, and you MUST do BOTH: (1) call maple_lookup with a `query` to get the ranked sources + URL templates (a source in the question's language is preferred); (2) build a concrete `url` from a template and call maple_lookup AGAIN with that `url` AND the `query` (the query pulls the matching sections out of long pages — keep it set to the skill/topic asked) to actually fetch and read that page. Never stop after step 1 — a directory of links is not an answer.");
            sb.AppendLine("- Then ANSWER the question directly from what the page says: name the actual skills/values (e.g. the top recommended link skills and their pick rates) in a sentence or two, in the user's language, and mention the source briefly. NEVER reply with only a link, a generic closer, or 'go check the site' — pulling the answer out of the page is your job.");
            sb.AppendLine("- The question's language decides the source: a Korean question prefers a Korean site. Map the class name to the English URL slug yourself (e.g. 히어로 → hero, 아란 → aran).");
            sb.AppendLine("Available game sources:");
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
