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
    public double JumpHeight { get; set; } = 50;     // max vertical reach to grab a ladder, logical px
    public double WalkSpeed { get; set; } = 90;      // px / second
    public double ClimbSpeed { get; set; } = 70;     // px / second
    public double Gravity { get; set; } = 900;       // px / second^2
    public double RoamingChance { get; set; } = 35;  // 0..100 %: chance to climb a ladder it passes
    public int TargetFps { get; set; } = 60;
    public double WorldPollHz { get; set; } = 8;     // how often window geometry is re-read
    public string SpriteSheet { get; set; } = "Assets/pet-spritesheet.png";

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
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall through to defaults on any parse/IO error.
        }

        var defaults = new Settings();
        try { File.WriteAllText(path, JsonSerializer.Serialize(defaults, Options)); }
        catch { /* best effort */ }
        return defaults;
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
        RoamingChance = double.IsFinite(RoamingChance) ? Math.Clamp(RoamingChance, 0, 100) : d.RoamingChance;
        TargetFps = TargetFps is >= 1 and <= 240 ? TargetFps : d.TargetFps;
        if (string.IsNullOrWhiteSpace(SpriteSheet)) SpriteSheet = d.SpriteSheet;

        static double Positive(double value, double fallback) =>
            double.IsFinite(value) && value > 0 ? value : fallback;
    }
}
