namespace MaplePet.Engine;

/// <summary>
/// The data a <c>/rank</c>-style lookup hands to the core renderer — the seam between <b>extraction</b>
/// (which server, which endpoints, how to parse) and <b>rendering</b> (the EXP bar + text layout the pet
/// speaks). It's the superset of what the bundled servers expose: GMS adds a true global <see cref="Rank"/>,
/// TMS adds a Legion <see cref="LegionGrade"/>, maple.gg adds <see cref="Fame"/>/<see cref="Guild"/>. Every
/// optional field is simply omitted from the rendered bubble when null/zero.
///
/// A user <c>kind:script</c> command produces one of these by calling the sandbox's <c>rank({…})</c> host
/// function; <c>RankRenderer</c> (Api/Chat) turns it into the spoken text. Pure data, no UI dependency.
/// </summary>
public sealed record RankView(
    string Name,
    int Level,
    string Job,
    string World,
    double? ExpPercent = null,   // progress through the current level, 0–100; null ⇒ no EXP bar (e.g. level cap)
    string? Guild = null,
    long? Rank = null,           // global ranking position (GMS); null/0 ⇒ omitted
    int? LegionLevel = null,     // Legion/Union level; null/0 ⇒ omitted
    string? LegionGrade = null,  // Legion grade label (TMS); shown beside the level when present
    int? Fame = null,            // popularity / fame
    string? ImageUrl = null,     // full-body character render (shown above the bubble text)
    string? InfoTitle = null,    // label for the "more info" link button (e.g. "maple.gg")
    string? InfoUrl = null,      // profile URL for the link button
    string? ServerLabel = null); // e.g. "GMS NA", "KMS", "MSEA", "TMS" — the headline server line
