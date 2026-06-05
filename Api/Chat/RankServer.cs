using System.Collections.Generic;
using System.Linq;

namespace MaplePet.Api.Chat;

/// <summary>Which client serves a <c>/rank</c> lookup: the keyless GMS rankings (<see cref="NexonGmsRankApi"/>),
/// the maple.gg profile scrape (<see cref="MapleGgScraper"/>), or the maple-kit proxy (<see cref="MapleKitApi"/>).</summary>
public enum RankSourceKind { Gms, MapleGg, MapleKit }

/// <summary>
/// A <c>/rank</c> target, chosen by a leading flag (e.g. <c>/rank -kr Name</c>). Every server is keyless.
/// <see cref="DataUrl"/> is what the source fetches: the GMS ranking base, or a <c>{0}</c>-templated URL
/// (maple.gg profile page / maple-kit API). <see cref="ServerCode"/> is the GMS sub-server (<c>na</c>/<c>eu</c>),
/// empty otherwise. <see cref="InfoSite"/> + <see cref="InfoUrlFormat"/> give the human profile page for the
/// "check more info on …" link (a single <c>{0}</c> for the URL-encoded character name).
/// </summary>
public sealed record RankServer(
    string Flag, string Label, RankSourceKind Kind, string DataUrl, string ServerCode,
    string InfoSite, string InfoUrlFormat);

/// <summary>The flag → server table for <c>/rank</c>. The server is picked per call by a leading flag; with
/// no flag it defaults to <c>-na</c> (GMS North America). All sources are keyless.</summary>
public static class RankServers
{
    private const string GmsRankingBase = "https://www.nexon.com/api/maplestory/no-auth/ranking/v2";

    public static readonly IReadOnlyList<RankServer> All = new[]
    {
        new RankServer("na",  "GMS NA", RankSourceKind.Gms,     GmsRankingBase, "na", "MapleRanks",    "https://mapleranks.com/u/{0}"),
        new RankServer("eu",  "GMS EU", RankSourceKind.Gms,     GmsRankingBase, "eu", "MapleRanks",    "https://mapleranks.com/u/{0}"),
        new RankServer("kr",  "KMS",    RankSourceKind.MapleGg,  "https://maple.gg/u/{0}",      "", "maple.gg",      "https://maple.gg/u/{0}"),
        new RankServer("sea", "MSEA",   RankSourceKind.MapleGg,  "https://msea.maple.gg/u/{0}", "", "maple.gg",      "https://msea.maple.gg/u/{0}"),
        new RankServer("tw",  "TMS",    RankSourceKind.MapleKit, "https://maple-kit.com/api/character?character_name={0}", "", "maple-kit.com", "https://maple-kit.com/character/{0}"),
    };

    /// <summary>The server used when <c>/rank</c> is given no flag.</summary>
    public static RankServer Default => All[0]; // -na

    /// <summary>Resolve a flag token (already lowercased, no dashes) to a <see cref="RankServer"/>, or null.</summary>
    public static RankServer? Resolve(string? flag)
        => flag is null ? null : All.FirstOrDefault(s => s.Flag == flag);

    /// <summary>The usable flags joined for help/error text, e.g. "-na, -eu, -kr, -sea, -tw".</summary>
    public static string FlagList => string.Join(", ", All.Select(s => "-" + s.Flag));
}
