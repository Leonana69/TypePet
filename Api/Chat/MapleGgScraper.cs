using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
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
    /// <see cref="Popularity"/> are null when the profile omits them (e.g. guildless characters);
    /// <see cref="ImageUrl"/> is the full-body character render shown in the bubble + history.</summary>
    public sealed record MapleGgRank(
        string Name, int Level, string Class, string World, string? Guild, int? Popularity, string? ImageUrl);

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
    /// region's <c>{0}</c>-templated profile URL (e.g. <c>https://maple.gg/u/{0}</c>). Throws
    /// <see cref="RankException"/> with a user-facing message on any failure or when the character
    /// doesn't exist.</summary>
    public async Task<MapleGgRank> GetRankAsync(string characterName, string urlFormat, CancellationToken ct)
    {
        string url = string.Format(urlFormat, Uri.EscapeDataString(characterName));
        string html = await GetHtmlAsync(url, characterName, ct).ConfigureAwait(false);
        return ParseProfile(html, characterName);
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
