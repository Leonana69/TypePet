namespace TypePet.Api.Chat;

/// <summary>
/// Builds the right <see cref="IChatBackend"/> for a provider profile. Kept free of Settings/Platform
/// types: the caller resolves the active profile and its key (from the secret store) and passes plain
/// values. Anything that isn't <c>"anthropic"</c> uses the OpenAI-compatible backend, which covers
/// OpenAI, DeepSeek, and local servers by base URL.
/// </summary>
public static class ChatBackendFactory
{
    public const string KindAnthropic = "anthropic";
    public const string KindOpenAi = "openai";

    public static IChatBackend Create(string? kind, string baseUrl, string apiKey, string model) =>
        (kind?.Trim().ToLowerInvariant()) switch
        {
            KindAnthropic => new AnthropicBackend(apiKey),
            _ => new OpenAiCompatibleBackend(baseUrl, apiKey, model),
        };
}
