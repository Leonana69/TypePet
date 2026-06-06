using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaplePet.Engine;

/// <summary>
/// User-editable configuration (see IMPLEMENTATION_PLAN.md section 7). Loaded from
/// <c>settings.json</c>; a missing/invalid file falls back to defaults and is rewritten.
/// </summary>
public sealed class Settings
{
    public double JumpHeight { get; set; } = 150;    // max vertical reach to jump onto a higher platform, logical px
    public double WalkSpeed { get; set; } = 90;      // px / second
    public double ClimbSpeed { get; set; } = 70;     // px / second
    public double Gravity { get; set; } = 900;       // px / second^2
    public double RoamingLevel { get; set; } = 50;   // 0..100: how restless it is — the chance it wanders
                                                     // to a new spot when idle (0 = stay put, 100 = always roam)
    public bool ShowOverlay { get; set; } = false;   // draw the debug window-edge / path overlay
    public bool HideWhenFullscreen { get; set; } = true; // hide the pet while a borderless/exclusive
                                                         // fullscreen app (a game/video) is foreground
    public int TargetFps { get; set; } = 60;
    public double WorldPollHz { get; set; } = 8;     // how often window geometry is re-read
    public string SpriteSheet { get; set; } = "Assets/pet-spritesheet.png";
    public string CurrentCharacterId { get; set; } = "default"; // selected character (CharacterStore id), or "default"

    public bool EnableMcpServer { get; set; } = false; // expose the pet over a local MCP tool server (LLM control)
    public int McpPort { get; set; } = 8765;           // localhost port the MCP server listens on when enabled

    // Global shortcut that pops up the floating "say" input box (you can also double-click the pet).
    // A compact gesture string like "Ctrl+Alt+Space"; parsed at the registration site, not here.
    public string SayInputHotkey { get; set; } = "Ctrl+Alt+Space";

    // ---- In-app chatbot (talks to an LLM provider with the user's own key) -------------------------
    public bool EnableChatbot { get; set; } = false;          // route the input bar to the LLM (vs plain "say")
    public List<ProviderProfile> Providers { get; set; } = new(); // configured LLM providers (seeded on first run)
    public string ActiveProviderId { get; set; } = "";        // which profile the chat uses (a Providers[].Id)
    public bool EnableWebSearch { get; set; } = true;         // keyless DuckDuckGo web_search + web_fetch tools
    public bool ChatHistoryVisible { get; set; } = true;      // input bar shows the conversation-history panel
    // The input bar's open shortcut is SayInputHotkey (above). Provider API keys are NOT stored here —
    // they live encrypted in the platform secret store, keyed by the provider id. Web search is keyless.

    // The /rank chat command picks its MapleStory server per call via a leading flag (-na/-eu/-kr/-sea/-tw,
    // default -na) — see RankServers — so the server is no longer a persisted setting.

    /// <summary>Path this instance was loaded from, used by <see cref="Save"/>. Not serialized.</summary>
    [JsonIgnore] public string SourcePath { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, Options);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    loaded.SourcePath = path;
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall through to defaults on any parse/IO error.
        }

        var defaults = new Settings { SourcePath = path };
        try { File.WriteAllText(path, JsonSerializer.Serialize(defaults, Options)); }
        catch { /* best effort */ }
        return defaults;
    }

    /// <summary>Sanitize and persist the current values back to <see cref="SourcePath"/>.</summary>
    public void Save()
    {
        Sanitize();
        if (string.IsNullOrEmpty(SourcePath)) return;
        try { File.WriteAllText(SourcePath, JsonSerializer.Serialize(this, Options)); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Replace any out-of-range, NaN, or Infinity value with its default. A user-edited or
    /// corrupted-but-parseable file could otherwise feed e.g. Gravity &lt;= 0 (pet never lands)
    /// or NaN (pet position becomes NaN and it vanishes) straight into the physics.
    /// </summary>
    private void Sanitize()
    {
        var d = new Settings();
        JumpHeight = Positive(JumpHeight, d.JumpHeight);
        WalkSpeed = Positive(WalkSpeed, d.WalkSpeed);
        ClimbSpeed = Positive(ClimbSpeed, d.ClimbSpeed);
        Gravity = Positive(Gravity, d.Gravity);
        WorldPollHz = Positive(WorldPollHz, d.WorldPollHz);
        RoamingLevel = double.IsFinite(RoamingLevel) ? Math.Clamp(RoamingLevel, 0, 100) : d.RoamingLevel;
        TargetFps = TargetFps is >= 1 and <= 240 ? TargetFps : d.TargetFps;
        McpPort = McpPort is >= 1 and <= 65535 ? McpPort : d.McpPort;
        if (string.IsNullOrWhiteSpace(SpriteSheet)) SpriteSheet = d.SpriteSheet;
        if (string.IsNullOrWhiteSpace(CurrentCharacterId)) CurrentCharacterId = d.CurrentCharacterId;
        if (string.IsNullOrWhiteSpace(SayInputHotkey)) SayInputHotkey = d.SayInputHotkey;

        // ---- chatbot ----
        Providers ??= new();
        if (Providers.Count == 0) SeedDefaultProviders();
        foreach (var p in Providers) p.Sanitize();

        // Migration: the OpenAI-compatible preset was renamed from "OpenAI" and is now shown first.
        var oai = Providers.FirstOrDefault(p => p.Id == "openai");
        if (oai is not null)
        {
            if (oai.DisplayName == "OpenAI") oai.DisplayName = "OpenAI Compatible";
            if (Providers.IndexOf(oai) > 0) { Providers.Remove(oai); Providers.Insert(0, oai); }
        }

        if (string.IsNullOrWhiteSpace(ActiveProviderId) || Providers.All(p => p.Id != ActiveProviderId))
            ActiveProviderId = Providers.Count > 0 ? Providers[0].Id : "";

        static double Positive(double value, double fallback) =>
            double.IsFinite(value) && value > 0 ? value : fallback;
    }

    /// <summary>Seed the two prioritized presets on first run (keys are added by the user in Settings).</summary>
    private void SeedDefaultProviders()
    {
        Providers.Add(new ProviderProfile
        {
            Id = "openai", DisplayName = "OpenAI Compatible", Kind = "openai",
            BaseUrl = "https://api.openai.com/v1", Model = "gpt-5.1", UsesKey = true, MaxTokens = 2048,
        });
        Providers.Add(new ProviderProfile
        {
            Id = "claude", DisplayName = "Claude", Kind = "anthropic",
            BaseUrl = "", Model = "claude-opus-4-8", UsesKey = true, MaxTokens = 2048,
        });
    }
}

/// <summary>
/// One configured LLM provider the chatbot can use. <see cref="Kind"/> selects the backend
/// (<c>"anthropic"</c> → Claude SDK; anything else → the OpenAI-compatible backend, so OpenAI, DeepSeek,
/// Ollama, and LM Studio are all just a <see cref="BaseUrl"/> + <see cref="Model"/>). The API key is NOT
/// stored here — it lives in the platform secret store under this profile's <see cref="Id"/>.
/// </summary>
public sealed class ProviderProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "openai";   // "anthropic" | "openai"
    public string BaseUrl { get; set; } = "";       // e.g. https://api.openai.com/v1, http://localhost:11434/v1
    public string Model { get; set; } = "";
    public bool UsesKey { get; set; } = true;        // false for keyless local servers (Ollama / LM Studio)
    public int MaxTokens { get; set; } = 2048;

    public void Sanitize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N")[..8];
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Id;
        Kind = (Kind?.Trim().ToLowerInvariant()) == "anthropic" ? "anthropic" : "openai";
        MaxTokens = MaxTokens is >= 256 and <= 32000 ? MaxTokens : 2048;
        BaseUrl ??= "";
        Model ??= "";
    }
}
