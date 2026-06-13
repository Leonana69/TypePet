using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypePet.Engine;

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
    public List<string> Stand2CharacterIds { get; set; } = new(); // characters that idle in their two-handed
                                                                  // stance ("stand2") — per-card checkbox in the picker

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
    // The curated game reference sites (maple_lookup, keyless RAG) are always on — no toggle.
    // The input bar's open shortcut is SayInputHotkey (above). Provider API keys are NOT stored here —
    // they live encrypted in the platform secret store, keyed by the provider id. Web search is keyless.

    // ---- User command library (the hot-reloaded skill library) -------------------------------------
    public List<string> DisabledCommandIds { get; set; } = new(); // installed commands the user turned OFF (by CommandStore id)
    public bool EnableUserScripts { get; set; } = true;           // allow kind:script commands (sandboxed JS); off disables them all

    /// <summary>Per-command network grants. A <c>kind:script</c> command that declares <c>hosts:</c> may
    /// reach the network only after the user approves it in the Commands tab. Each grant pins the command's
    /// CommandStore id to the host-allowlist signature it was approved for, so editing <c>hosts:</c> later
    /// revokes the grant until it's re-approved. Empty by default ⇒ no command has network access.</summary>
    public List<NetworkGrant> NetworkApprovedCommands { get; set; } = new();

    // ---- /remind reminders -------------------------------------------------------------------------
    // Persisted so recurring (daily/weekly/monthly) reminders survive a restart. One-off reminders are
    // kept too; on launch a one-off whose time already passed while the app was closed is dropped, and a
    // recurring reminder recomputes its next fire from "now". Rewritten by the scheduler on every change.
    public List<ReminderRecord> Reminders { get; set; } = new();

    /// <summary>Path this instance was loaded from, used by <see cref="Save"/>. Not serialized.</summary>
    [JsonIgnore] public string SourcePath { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Write enums (e.g. a reminder's Kind/Weekday) as readable names rather than integers; numeric
        // values are still accepted on read. No other Settings field is an enum today.
        Converters = { new JsonStringEnumConverter() },
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

        Stand2CharacterIds ??= new();
        Stand2CharacterIds.RemoveAll(string.IsNullOrWhiteSpace);

        DisabledCommandIds ??= new();
        NetworkApprovedCommands ??= new();
        NetworkApprovedCommands.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Id));

        Reminders ??= new();
        foreach (var r in Reminders) r.Sanitize();

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

    /// <summary>True when character <paramref name="id"/> is set to idle in its two-handed stance
    /// ("stand2" — the picker card's top-left checkbox) rather than the default "stand1". Ids compare
    /// case-insensitively, matching how built-in ids resolve in <see cref="CharacterStore"/>.</summary>
    public bool UsesStand2(string? id) =>
        !string.IsNullOrEmpty(id)
        && Stand2CharacterIds.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Record whether character <paramref name="id"/> idles in its two-handed stance. The
    /// caller persists via <see cref="Save"/>.</summary>
    public void SetUsesStand2(string id, bool useStand2)
    {
        Stand2CharacterIds.RemoveAll(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase));
        if (useStand2) Stand2CharacterIds.Add(id);
    }

    /// <summary>True if command <paramref name="id"/> has a standing network grant whose approved host
    /// signature still matches <paramref name="hostsSignature"/> (the command's current <c>hosts:</c>).
    /// A changed allowlist won't match, so widening the hosts forces a fresh approval.</summary>
    public bool IsNetworkApproved(string id, string hostsSignature) =>
        NetworkApprovedCommands.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)
            && g.Hosts == hostsSignature);

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

/// <summary>A standing approval for one <c>kind:script</c> command to use the network. <see cref="Id"/> is
/// its CommandStore id (folder name); <see cref="Hosts"/> is the host-allowlist signature
/// (<see cref="CommandManifest.HostsSignature()"/>) the user approved — re-checked at run time so an edited
/// allowlist revokes the grant.</summary>
public sealed class NetworkGrant
{
    public string Id { get; set; } = "";
    public string Hosts { get; set; } = "";
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

/// <summary>How a <see cref="ReminderRecord"/> repeats. <see cref="Once"/> fires a single time at an
/// absolute moment; the others fire at a fixed clock time-of-day on a daily/weekly/monthly cadence.</summary>
public enum ReminderKind { Once, Daily, Weekly, Monthly }

/// <summary>
/// A persisted <c>/remind</c> reminder. A flat, JSON-friendly shape (enum→name, <see cref="TimeOfDay"/> as
/// <c>"20:00:00"</c>, <see cref="DueAt"/> native) following the <see cref="ProviderProfile"/> convention —
/// unused fields just carry defaults for the kind. The recurrence math lives here in
/// <see cref="NextOccurrence"/> so it's the single source of "when does this fire next", used both when a
/// reminder is first scheduled and when the scheduler re-arms a recurring one after it fires.
/// </summary>
public sealed class ReminderRecord
{
    /// <summary>Stable, user-facing id (shown by <c>/remind list</c>, used by <c>/remind cancel</c>);
    /// persisted so it survives a restart.</summary>
    public int Id { get; set; }
    public ReminderKind Kind { get; set; } = ReminderKind.Once;
    public string Message { get; set; } = "";

    /// <summary>For <see cref="ReminderKind.Once"/>, the absolute fire moment. For recurring kinds, a cache
    /// of the next computed fire (refreshed on each schedule/re-arm) — used for sorting and display.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>Recurring: the clock time-of-day to fire at (e.g. 20:00:00). Ignored for <see cref="ReminderKind.Once"/>.</summary>
    public TimeSpan TimeOfDay { get; set; }

    /// <summary>Weekly: which weekday to fire on.</summary>
    public DayOfWeek Weekday { get; set; } = DayOfWeek.Monday;

    /// <summary>Monthly: the day-of-month to fire on (1–31; clamped to the month's last day when shorter).</summary>
    public int DayOfMonth { get; set; } = 1;

    public void Sanitize()
    {
        if (!Enum.IsDefined(Kind)) Kind = ReminderKind.Once;
        Message ??= "";
        if (TimeOfDay < TimeSpan.Zero || TimeOfDay >= TimeSpan.FromDays(1)) TimeOfDay = TimeSpan.Zero;
        if (!Enum.IsDefined(Weekday)) Weekday = DayOfWeek.Monday;
        DayOfMonth = Math.Clamp(DayOfMonth, 1, 31);
    }

    /// <summary>The next fire time strictly after <paramref name="after"/>. Pure. For recurring kinds the
    /// time-of-day is re-materialized into a fresh <see cref="DateTime"/> each period (not by adding a 24h
    /// span to an instant) so it stays anchored to wall-clock across DST transitions.</summary>
    public DateTime NextOccurrence(DateTime after)
    {
        switch (Kind)
        {
            case ReminderKind.Daily:
            {
                var cand = DateAt(after.Date);
                if (cand <= after) cand = DateAt(after.Date.AddDays(1));
                return cand;
            }
            case ReminderKind.Weekly:
            {
                int delta = ((int)Weekday - (int)after.DayOfWeek + 7) % 7; // 0..6 days ahead
                var cand = DateAt(after.Date.AddDays(delta));
                if (cand <= after) cand = DateAt(after.Date.AddDays(delta + 7));
                return cand;
            }
            case ReminderKind.Monthly:
            {
                var cand = MonthlyCandidate(after.Year, after.Month, after.Kind);
                if (cand <= after)
                {
                    int y = after.Year, mo = after.Month + 1;
                    if (mo > 12) { mo = 1; y++; }
                    cand = MonthlyCandidate(y, mo, after.Kind);
                }
                return cand;
            }
            default: // Once
                return DueAt;
        }
    }

    private DateTime DateAt(DateTime date) =>
        new(date.Year, date.Month, date.Day, TimeOfDay.Hours, TimeOfDay.Minutes, TimeOfDay.Seconds, date.Kind);

    // Clamp the day to the month's length so "the 31st" still fires (on the last day) in shorter months.
    private DateTime MonthlyCandidate(int year, int month, DateTimeKind kind)
    {
        int day = Math.Min(DayOfMonth, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, TimeOfDay.Hours, TimeOfDay.Minutes, TimeOfDay.Seconds, kind);
    }
}
