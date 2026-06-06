using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>
/// A provider the chatbot can talk to. One implementation per wire format: <see cref="AnthropicBackend"/>
/// (Claude) and <see cref="OpenAiCompatibleBackend"/> (OpenAI + everything OpenAI-compatible). The agent
/// loop calls <see cref="SendAsync"/> once per turn and runs any returned tool calls itself, so the
/// backend only has to translate one request/response — it doesn't own the loop.
/// </summary>
public interface IChatBackend
{
    /// <summary>False if this provider/model can't do function calling (some local models). The agent
    /// then offers no tools — chat still works, but the pet won't react and web search is unavailable.</summary>
    bool SupportsTools { get; }

    /// <summary>Send the conversation and return the assistant's turn (text + any tool calls to run).</summary>
    Task<ChatTurn> SendAsync(ChatRequest request, CancellationToken ct);

    /// <summary>List the model ids this provider offers (for the Settings model picklist). Best-effort:
    /// returns an empty list on any failure (no key, unsupported endpoint) so the UI falls back to free text.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
