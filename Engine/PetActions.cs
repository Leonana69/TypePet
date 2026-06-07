namespace MaplePet.Engine;

/// <summary>How a commanded action animation plays.</summary>
public enum ActionMode
{
    /// <summary>Play the pose's cycle <c>cycles</c> times, then revert to the pet's normal
    /// (state-driven) pose. Every commanded gesture plays at least twice so it reads clearly.</summary>
    Once,
    /// <summary>Loop the pose and hold it until it is cleared or the pet is commanded to move.</summary>
    Hold,
}

/// <summary>
/// The kind of attack a commandable action triggers. Attack actions don't map to a single fixed pose:
/// at trigger time one of the worn character's available stances for the kind is chosen at random (see
/// <see cref="Attacks"/>), and the strike is followed by a short "alert" hold. <see cref="None"/> marks
/// an ordinary (non-attack) action with a fixed <see cref="ActionDef.Pose"/>.
/// </summary>
public enum AttackKind { None, Any, Stab, Swing, Shoot }

/// <summary>
/// The footage poses that make up each attack kind. A character supports whichever subset its footage
/// actually defines (a bare-handed default has only swing/stab; a gunner adds shoot); the control layer
/// filters these against the worn character's poses and picks one at random. The names mirror the
/// attack stances exported by maple-character-builder (one-handed <c>O</c>, two-handed
/// <c>T</c>, polearm <c>P</c>). <c>proneStab</c> is deliberately NOT here — it's a distinct lie-down
/// gesture (the <c>prone_stab</c> action), not a standing attack the random picker should land on.
/// </summary>
public static class Attacks
{
    /// <summary>One-/two-handed stab stances.</summary>
    public static readonly IReadOnlyList<string> Stab = new[] { "stabO1", "stabO2", "stabT1", "stabT2" };

    /// <summary>One-/two-handed and polearm swing stances.</summary>
    public static readonly IReadOnlyList<string> Swing = new[]
        { "swingO1", "swingO2", "swingO3", "swingT1", "swingT2", "swingT3", "swingP1", "swingP2" };

    /// <summary>Ranged (bow/gun/claw) shoot stances.</summary>
    public static readonly IReadOnlyList<string> Shoot = new[] { "shoot1", "shoot2", "shootF" };

    /// <summary>The concrete attack kinds, in a stable order — used to enumerate a character's options
    /// (so "attack" can pick uniformly among the KINDS it can do, then a variant within).</summary>
    public static readonly IReadOnlyList<AttackKind> Kinds = new[] { AttackKind.Stab, AttackKind.Swing, AttackKind.Shoot };

    /// <summary>The candidate pose names for one attack kind (empty for <see cref="AttackKind.None"/>
    /// and <see cref="AttackKind.Any"/>, which spans several kinds — enumerate via <see cref="Kinds"/>).</summary>
    public static IReadOnlyList<string> Variants(AttackKind kind) => kind switch
    {
        AttackKind.Stab => Stab,
        AttackKind.Swing => Swing,
        AttackKind.Shoot => Shoot,
        _ => System.Array.Empty<string>(),
    };

    /// <summary>Every attack pose name across all kinds. The live pet decodes this whole set so any
    /// character's attacks are ready to play regardless of which subset it happens to define.</summary>
    public static readonly IReadOnlyCollection<string> All = BuildAll();

    private static IReadOnlyCollection<string> BuildAll()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Kinds)
            foreach (var p in Variants(k))
                set.Add(p);
        return set;
    }
}

/// <summary>
/// One controllable action: a friendly <paramref name="Name"/> the LLM uses, the footage
/// <paramref name="Pose"/> it maps to (empty for attack actions, whose pose is chosen per character at
/// trigger time), its default <paramref name="Mode"/>, whether it <paramref name="RequiresGrounded"/>
/// (rejected mid-air / on a ladder), a short human <paramref name="Description"/> surfaced through the
/// capability snapshot, and — for attacks — which <paramref name="Attack"/> kind it draws its random
/// pose from.
/// </summary>
public sealed record ActionDef(
    string Name, string Pose, ActionMode Mode, bool RequiresGrounded, string Description,
    AttackKind Attack = AttackKind.None)
{
    /// <summary>True for the stab/swing/shoot/attack actions: the pose isn't fixed — it's chosen at
    /// random from the worn character's matching <see cref="Attacks"/> stances, and the strike is
    /// followed by a brief "alert" hold (see <c>CharacterAnimator.BeginAttack</c>).</summary>
    public bool IsAttack => Attack != AttackKind.None;
}

/// <summary>
/// The data-driven vocabulary mapping LLM-friendly action names (and aliases) to footage poses, so the
/// control API never exposes cryptic manifest names like <c>swingO1</c>. The attack actions are a
/// single umbrella each — <c>attack</c> picks any available stance, <c>stab</c>/<c>swing</c>/<c>shoot</c>
/// pick within their kind — rather than one entry per variant; the concrete pose is resolved at runtime
/// against <c>CharacterSprites.PoseNames</c>. Adding a new gesture is a one-line edit here.
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
        new("attack",     "",          ActionMode.Once, true,  "Attack: randomly stab, swing, or shoot (whatever the character can do), then stay alert for a few seconds.", AttackKind.Any),
        new("stab",       "",          ActionMode.Once, true,  "Stab attack (uses one of the character's stab moves).", AttackKind.Stab),
        new("swing",      "",          ActionMode.Once, true,  "Swing attack (uses one of the character's swing moves).", AttackKind.Swing),
        new("shoot",      "",          ActionMode.Once, true,  "Ranged attack: shoot (only if the character carries a ranged weapon).", AttackKind.Shoot),
        new("prone_stab", "proneStab", ActionMode.Once, true,  "Attack while lying down."),
    };

    private static readonly Dictionary<string, ActionDef> ByName = BuildIndex();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lie"] = "prone", ["lie_down"] = "prone", ["liedown"] = "prone", ["crouch"] = "prone",
        ["rest"] = "sit", ["startle"] = "alert",
        ["hit"] = "attack", ["fight"] = "attack",
        ["slash"] = "swing", ["fire"] = "shoot", ["shoot_arrow"] = "shoot",
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
