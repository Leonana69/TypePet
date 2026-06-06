using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>
/// A tiny, keyless client for the <b>KMS</b> and <b>MSEA</b> <c>/rank</c> lookups. Unlike KMS/SEA on the
/// Nexon Open API (which needs a per-region key), these go straight to the public community profile site
/// <b>maple.gg</b> and "web grab" the character page:
///
///   <c>GET https://maple.gg/u/{name}</c> (KMS)  ·  <c>GET https://msea.maple.gg/u/{name}</c> (MSEA)
///
/// Both are dak.gg Next.js apps that server-render the character's summary card, so a single page fetch
/// gives us the name, level, class, world, popularity/fame, guild and the character render image without
/// any API key. We parse the stable, human-readable element classes (<c>nickname</c>, <c>level</c>,
/// <c>job</c>, <c>world</c>, <c>popularity</c>, <c>guild</c>, <c>character-image</c>) — never the hashed
/// styled-component classes, which change on every deploy. KMS renders Korean labels/values; MSEA renders
/// English. A nonexistent name HTTP-307-redirects to <c>/search</c>, which we surface as "not found".
/// </summary>
public sealed class MapleGgScraper
{
    /// <summary>The fields <c>/rank</c> shows for a maple.gg lookup. <see cref="Guild"/> and
    /// <see cref="Popularity"/> (Fame) are null when the profile omits them (e.g. guildless characters);
    /// <see cref="ImageUrl"/> is the full-body character render shown in the bubble + history.
    /// <see cref="ExpPercent"/> (most-recent EXP-history %), <see cref="Rank"/> (global overall rank) and
    /// <see cref="LegionLevel"/> come from a best-effort dak.gg API call (the page itself doesn't carry them)
    /// and are null if it fails / the character isn't ranked / has no Union / is at the level cap.</summary>
    public sealed record MapleGgRank(
        string Name, int Level, string Class, string World, string? Guild, int? Popularity, string? ImageUrl,
        double? ExpPercent = null, long? Rank = null, int? LegionLevel = null);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // AllowAutoRedirect=false so a missing-character 307→/search surfaces as a redirect we can detect
        // (rather than silently following it to the search page). AutomaticDecompression covers an
        // edge/CDN that honours our browser-like UA and gzips the response.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ko;q=0.8");
        // A normal browser UA avoids naive bot filtering at the edge.
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 MaplePet");
        return c;
    }

    // SSR markers and the summary-card fields. Singleline so '.' spans the tags between a card class and
    // its inner <span> (the page is one long line anyway). Each capture is the FIRST match; every class is
    // unique to the summary card on both sites, so there is no risk of matching a different element.
    private static readonly Regex CommentRx     = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NameRx        = new(@"class=""nickname"">([^<]*)</div>", RegexOptions.Compiled);
    private static readonly Regex LevelRx       = new(@"class=""level"">\s*Lv\.\s*([\d,]+)", RegexOptions.Compiled);
    private static readonly Regex JobRx         = new(@"class=""job"">.*?<span>([^<]*)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WorldRx       = new(@"class=""world"">.*?<span>([^<]*)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex GuildRx       = new(@"class=""guild"">.*?<span>([^<]*)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PopularityRx  = new(@"class=""popularity"">([^<]*)</span>", RegexOptions.Compiled);
    private static readonly Regex ImageTagRx    = new(@"<img[^>]*class=""character-image""[^>]*>", RegexOptions.Compiled);
    private static readonly Regex SrcRx         = new(@"\bsrc=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex DigitsRx      = new(@"[\d,]+", RegexOptions.Compiled);

    /// <summary>Look <paramref name="characterName"/> up on maple.gg. <paramref name="urlFormat"/> is the
    /// region's <c>{0}</c>-templated profile URL (e.g. <c>https://maple.gg/u/{0}</c>); <paramref
    /// name="statsUrlFormat"/> is its <c>{0}</c>-templated dak.gg JSON API URL for EXP%/rank/Legion
    /// (e.g. <c>https://maple.dakgg.io/api/v1/characters/{0}/profile</c>) — best-effort, skipped when empty.
    /// Throws <see cref="RankException"/> with a user-facing message on any failure or when the character
    /// doesn't exist.</summary>
    public async Task<MapleGgRank> GetRankAsync(string characterName, string urlFormat, string statsUrlFormat, CancellationToken ct)
    {
        string url = string.Format(urlFormat, Uri.EscapeDataString(characterName));
        string html = await GetHtmlAsync(url, characterName, ct).ConfigureAwait(false);
        var rank = ParseProfile(html, characterName);

        // The page's SSR HTML carries only the identity card; EXP%, rank and Legion come from the dak.gg JSON
        // API the page calls client-side. Fetch them with the resolved (canonical) name; any failure leaves
        // them null so the core lookup still succeeds.
        if (!string.IsNullOrEmpty(statsUrlFormat))
        {
            string statsUrl = string.Format(statsUrlFormat, Uri.EscapeDataString(rank.Name));
            var (expPct, globalRank, legion) = await TryStatsAsync(statsUrl, ct).ConfigureAwait(false);
            rank = rank with { ExpPercent = expPct, Rank = globalRank, LegionLevel = legion };
        }
        return rank;
    }

    /// <summary>Best-effort EXP% + global rank + Legion level from the dak.gg JSON API (the profile endpoint):
    /// the most recent <c>characterExpLogs</c> point's percentage, <c>totalRank.rank</c> (global overall rank)
    /// and <c>unionRank.n4level</c> (Legion/Union level). Any failure (or a character with no ranking / no
    /// Union) returns nulls rather than sinking the lookup.</summary>
    private static async Task<(double? expPct, long? rank, int? legion)> TryStatsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            // The API is served for the maple.gg SPA; a matching Referer avoids any origin gating at the edge.
            req.Headers.TryAddWithoutValidation("Referer",
                url.Contains("msea.dakgg.io", StringComparison.Ordinal) ? "https://msea.maple.gg/" : "https://maple.gg/");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null, null);

            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, default, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            // Each field is read only after a ValueKind==Number guard: TryGetInt32/64/Double THROW (not
            // return false) on a string/null element, and a throw here would fall to the catch below and drop
            // the OTHER fields too — so every field stays independently best-effort.
            long? rank = root.TryGetProperty("totalRank", out var tr) && tr.ValueKind == JsonValueKind.Object
                && tr.TryGetProperty("rank", out var rv) && rv.ValueKind == JsonValueKind.Number
                && rv.TryGetInt64(out var rn) && rn > 0 ? rn : null;

            int? legion = root.TryGetProperty("unionRank", out var ur) && ur.ValueKind == JsonValueKind.Object
                && ur.TryGetProperty("n4level", out var nl) && nl.ValueKind == JsonValueKind.Number
                && nl.TryGetInt32(out var nlv) && nlv > 0 ? nlv : null;

            double? expPct = MostRecentExp(root);
            return (expPct, rank, legion);
        }
        catch { return (null, null, null); }
    }

    /// <summary>The most recent EXP-history point's percentage (the "EXP History" chart's latest value), or
    /// null if absent or the character is at the level cap. Each <c>characterExpLogs</c> entry is
    /// <c>[timestampMs, level, exp%, rawExp]</c>; at the cap (300) the % is 0, so it's omitted like GMS/TMS.</summary>
    private static double? MostRecentExp(JsonElement root)
    {
        if (!root.TryGetProperty("characterExpLogs", out var logs) || logs.ValueKind != JsonValueKind.Array
            || logs.GetArrayLength() == 0)
            return null;
        var last = logs[logs.GetArrayLength() - 1];
        // Guard that both fields we read are present numbers — TryGetDouble THROWS on a string/null element.
        if (last.ValueKind != JsonValueKind.Array || last.GetArrayLength() < 3
            || last[1].ValueKind != JsonValueKind.Number || last[2].ValueKind != JsonValueKind.Number)
            return null;
        // Omit at the level cap (300), where the % is 0. Read the level as a double so a float-encoded 300.0
        // still trips the gate.
        if (last[1].TryGetDouble(out var lv) && lv >= 300) return null;
        return last[2].TryGetDouble(out var pct) ? pct : null;
    }

    /// <summary>GET <paramref name="url"/> and return the page HTML. A redirect (307→/search) means the
    /// character doesn't exist; transport failures and other non-2xx responses become a friendly
    /// <see cref="RankException"/>.</summary>
    private static async Task<string> GetHtmlAsync(string url, string characterName, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(url, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { throw new RankException("maple.gg timed out — try again."); }
        catch (HttpRequestException ex) { throw new RankException($"Couldn't reach maple.gg ({ex.Message})."); }

        using (resp)
        {
            // A nonexistent name redirects to /search — there's no profile, so report it as not found.
            if ((int)resp.StatusCode is >= 300 and < 400)
                throw new RankException($"Character \"{characterName}\" not found on maple.gg.");
            if (!resp.IsSuccessStatusCode)
                throw new RankException($"maple.gg request failed ({(int)resp.StatusCode}).");
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Parse a maple.gg character page into a <see cref="MapleGgRank"/>. A page with no nickname
    /// card isn't a valid profile, so it's reported as "not found". Pure (no I/O) so it can be tested
    /// against saved pages.</summary>
    internal static MapleGgRank ParseProfile(string html, string characterName)
    {
        // Strip React's SSR comment markers (it inserts "<!-- -->" between text nodes, e.g. "Lv.<!-- -->300").
        string h = CommentRx.Replace(html, "");

        // The nickname card is rendered for every real character; its absence means no such character.
        string? name = Decode(Group(NameRx, h));
        if (name is null)
            throw new RankException($"Character \"{characterName}\" not found on maple.gg.");

        int level = int.TryParse(Group(LevelRx, h)?.Replace(",", ""), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var lv) ? lv : 0;
        string cls = Decode(Group(JobRx, h)) ?? "?";
        string world = Decode(Group(WorldRx, h)) ?? "?";
        string? guild = Decode(Group(GuildRx, h));

        // The popularity/fame label varies by language ("인기도 99,999" / "Fame 133"); take the number out of it.
        int? popularity = null;
        if (Group(PopularityRx, h) is { } popText && DigitsRx.Match(popText) is { Success: true } dm
            && int.TryParse(dm.Value.Replace(",", ""), out var pv))
            popularity = pv;

        string? image = ImageTagRx.Match(h) is { Success: true } tag && SrcRx.Match(tag.Value) is { Success: true } src
            ? src.Groups[1].Value : null;

        return new MapleGgRank(name, level, cls, world, guild, popularity, NullIfEmpty(image));
    }

    private static string? Group(Regex rx, string s)
    {
        var m = rx.Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Decode(string? s) => s is null ? null : NullIfEmpty(WebUtility.HtmlDecode(s).Trim());

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
