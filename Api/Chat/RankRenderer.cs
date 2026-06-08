using System;
using System.Collections.Generic;
using TypePet.Engine;

namespace TypePet.Api.Chat;

/// <summary>
/// Renders a <see cref="RankView"/> into the plain multi-line text the pet speaks for a <c>/rank</c> lookup.
/// This is the "render in core" half of the rank feature: data extraction can live in a user
/// <c>kind:script</c> command, but the layout — and the fixed-width EXP bar that the macOS monospace fix
/// (<c>Rendering/MonoGlyphs</c>) depends on — stays here so every source renders identically. The three
/// built-in formatters in <see cref="ChatCommands"/> produce the same shape; they collapse into this when
/// the built-in <c>/rank</c> is retired.
/// </summary>
public static class RankRenderer
{
    public static string Format(RankView v)
    {
        var lines = new List<string> { $"{v.Name} · Lv.{v.Level} · {v.Job}" };
        // EXP progress through the current level (omitted at the cap / when the source can't supply it).
        if (v.ExpPercent is double pct) lines.Add(ExpBar(pct));
        lines.Add($"World: {v.World}");
        // Headline line: the server label, with the global rank when the source carries one (GMS).
        string label = (v.ServerLabel ?? "").Trim();
        if (v.Rank is long rk && rk > 0)
            lines.Add(label.Length > 0 ? $"{label} · Rank #{rk:N0}" : $"Rank #{rk:N0}");
        else if (label.Length > 0)
            lines.Add(label);
        if (!string.IsNullOrWhiteSpace(v.Guild)) lines.Add($"Guild: {v.Guild}");
        if (v.LegionLevel is int legion && legion > 0)
            lines.Add($"Legion Lv.{legion:N0}" + (string.IsNullOrWhiteSpace(v.LegionGrade) ? "" : $" · {v.LegionGrade}"));
        if (v.Fame is int fame) lines.Add($"Fame {fame}");
        return string.Join("\n", lines);
    }

    /// <summary>A fixed-width EXP progress bar from a 0–100 percentage, drawn with full/empty block cells
    /// (e.g. <c>EXP ██████░░░░ 60.05%</c>) since the bubble renders the result as plain text. Mirrors the
    /// built-in <c>/rank</c> bar exactly so JS- and C#-sourced lookups look the same.</summary>
    public static string ExpBar(double pct)
    {
        const int width = 10;
        int filled = (int)Math.Round(pct / 100.0 * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return $"EXP {new string('█', filled)}{new string('░', width - filled)} {pct:0.00}%";
    }
}
