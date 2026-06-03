namespace MaplePet.Engine;

/// <summary>How a commanded action animation plays.</summary>
public enum ActionMode
{
    /// <summary>Play the pose's cycle once, then revert to the pet's normal (state-driven) pose.</summary>
    Once,
    /// <summary>Loop the pose and hold it until it is cleared or the pet is commanded to move.</summary>
    Hold,
}

/// <summary>
/// One controllable action: a friendly <paramref name="Name"/> the LLM uses, the footage
/// <paramref name="Pose"/> it maps to, its default <paramref name="Mode"/>, whether it
/// <paramref name="RequiresGrounded"/> (rejected mid-air / on a ladder), and a short human
/// <paramref name="Description"/> surfaced through the capability snapshot for tool-schema docs.
/// </summary>
public sealed record ActionDef(string Name, string Pose, ActionMode Mode, bool RequiresGrounded, string Description);

/// <summary>
/// The data-driven vocabulary mapping LLM-friendly action names (and aliases) to footage poses, so
/// the control API never exposes cryptic manifest names like <c>swingO1</c>. Adding a new gesture is
/// a one-line edit here. The poses a given character actually supports are filtered at runtime
/// against <c>CharacterSprites.PoseNames</c>.
/// </summary>
public static class ActionRegistry
{
    private static readonly ActionDef[] Defs =
    {
        new("prone",      "prone",     ActionMode.Hold, true,  "Lie face-down on the ground."),
        new("sit",        "sit",       ActionMode.Hold, true,  "Sit down (not all characters have this)."),
        new("alert",      "alert",     ActionMode.Once, true,  "Startle / look alert."),
        new("heal",       "heal",      ActionMode.Once, true,  "Play a healing animation."),
        new("fly",        "fly",       ActionMode.Hold, false, "Float / fly in place."),
        new("attack",     "swingO1",   ActionMode.Once, true,  "Swing a weapon (attack)."),
        new("swing2",     "swingO2",   ActionMode.Once, true,  "A second swing-attack variant."),
        new("swing3",     "swingO3",   ActionMode.Once, true,  "A third swing-attack variant."),
        new("stab",       "stabO1",    ActionMode.Once, true,  "Stab a weapon (attack)."),
        new("stab2",      "stabO2",    ActionMode.Once, true,  "A second stab-attack variant."),
        new("prone_stab", "proneStab", ActionMode.Once, true,  "Attack while lying down."),
    };

    private static readonly Dictionary<string, ActionDef> ByName = BuildIndex();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lie"] = "prone", ["lie_down"] = "prone", ["liedown"] = "prone", ["crouch"] = "prone",
        ["rest"] = "sit", ["startle"] = "alert", ["swing"] = "attack",
    };

    /// <summary>Every defined action (unfiltered by character).</summary>
    public static IReadOnlyCollection<ActionDef> All => Defs;

    /// <summary>Resolve a name or alias (case-insensitive) to its definition, or null if unknown.</summary>
    public static ActionDef? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = name.Trim();
        if (ByName.TryGetValue(key, out var def)) return def;
        if (Aliases.TryGetValue(key, out var canonical) && ByName.TryGetValue(canonical, out var aliased))
            return aliased;
        return null;
    }

    private static Dictionary<string, ActionDef> BuildIndex()
    {
        var d = new Dictionary<string, ActionDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in Defs) d[def.Name] = def;
        return d;
    }
}
