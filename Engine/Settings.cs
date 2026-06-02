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
    public double RoamingHeight { get; set; } = 100; // 0..100 %: max height it roams to (taskbar=0, screen top=100)
    public int TargetFps { get; set; } = 60;
    public double WorldPollHz { get; set; } = 8;     // how often window geometry is re-read
    public string SpriteSheet { get; set; } = "Assets/pet-spritesheet.png";

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
        RoamingHeight = double.IsFinite(RoamingHeight) ? Math.Clamp(RoamingHeight, 0, 100) : d.RoamingHeight;
        TargetFps = TargetFps is >= 1 and <= 240 ? TargetFps : d.TargetFps;
        if (string.IsNullOrWhiteSpace(SpriteSheet)) SpriteSheet = d.SpriteSheet;

        static double Positive(double value, double fallback) =>
            double.IsFinite(value) && value > 0 ? value : fallback;
    }
}
