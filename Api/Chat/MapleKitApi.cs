using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>
/// A tiny, keyless client for the <b>TMS</b> (Taiwan MapleStory) <c>/rank</c> lookup. TMS has a Nexon Open
/// API, but it needs a per-region key — so instead we use the community site <b>maple-kit.com</b>, whose
/// backing endpoint is a keyless <i>proxy</i> of that same Open API:
///
///   <c>GET https://maple-kit.com/api/character?character_name={name}</c>
///
/// It returns one combined JSON envelope using the Open API's own field names — <c>basic</c> (= the
/// <c>/character/basic</c> response), <c>popularity</c>, and <c>union</c> (Legion) — and the same
/// <c>{"error":{"name":"OPENAPI…"}}</c> failure envelope. So a single keyless call yields the full rich
/// profile (level, exp%, class, world, guild, union, popularity, render image) that the keyed API used to
/// need four calls and a key for. Values are Traditional Chinese (it's the Taiwan server).
/// </summary>
public sealed class MapleKitApi
{
    /// <summary>The fields <c>/rank</c> shows. Optional fields are null when absent in the response.</summary>
    public sealed record CharacterRank(
        string Name, string World, string Class, int Level, string ExpRate,
        string? Guild, string? CreatedDate, string? ImageUrl,
        int? Popularity, int? UnionLevel, string? UnionGrade);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        // The endpoint is browser-facing; a normal UA avoids any naive bot filtering at the edge.
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 MaplePet");
        return c;
    }

    /// <summary>Look <paramref name="characterName"/> up via maple-kit. <paramref name="urlFormat"/> is the
    /// region's <c>{0}</c>-templated API URL (e.g. <c>https://maple-kit.com/api/character?character_name={0}</c>).
    /// Throws <see cref="RankException"/> with a user-facing message on any failure or when the character
    /// doesn't exist.</summary>
    public async Task<CharacterRank> GetRankAsync(string characterName, string urlFormat, CancellationToken ct)
    {
        string url = string.Format(urlFormat, Uri.EscapeDataString(characterName));
        using var doc = await GetAsync(url, characterName, ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("basic", out var b) || b.ValueKind != JsonValueKind.Object)
            throw new RankException($"Character \"{characterName}\" not found.");

        string name = Str(b, "character_name") ?? characterName;
        string world = Str(b, "world_name") ?? "?";
        string cls = Str(b, "character_class") ?? "?";
        int level = b.TryGetProperty("character_level", out var lv) && lv.TryGetInt32(out var l) ? l : 0;
        string expRate = FormatExp(Str(b, "character_exp_rate"));
        string? guild = NullIfEmpty(Str(b, "character_guild_name"));
        string? created = ShortDate(Str(b, "character_date_create"));
        string? image = NullIfEmpty(Str(b, "character_image"));

        int? popularity = root.TryGetProperty("popularity", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("popularity", out var pv) && pv.TryGetInt32(out var pn) ? pn : null;

        int? unionLevel = null;
        string? unionGrade = null;
        if (root.TryGetProperty("union", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            if (u.TryGetProperty("union_level", out var ul) && ul.TryGetInt32(out var uln)) unionLevel = uln;
            unionGrade = NullIfEmpty(Str(u, "union_grade"));
        }

        return new CharacterRank(name, world, cls, level, expRate, guild, created, image, popularity, unionLevel, unionGrade);
    }

    /// <summary>GET <paramref name="url"/> and return the parsed JSON (the caller disposes it). The proxy
    /// passes the Open API's <c>{"error":{"name","message"}}</c> envelope (usually HTTP 400) straight
    /// through; those — and transport failures — become a friendly <see cref="RankException"/>
    /// (<paramref name="subject"/> names the character for "not found").</summary>
    private static async Task<JsonDocument> GetAsync(string url, string subject, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(url, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { throw new RankException("maple-kit.com timed out — try again."); }
        catch (HttpRequestException ex) { throw new RankException($"Couldn't reach maple-kit.com ({ex.Message})."); }

        using (resp)
        {
            JsonDocument doc;
            try
            {
                // Parse from the stream (the body is large and UTF-8); JsonDocument handles the bytes natively.
                await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                doc = await JsonDocument.ParseAsync(s, default, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RankException($"maple-kit.com returned an unreadable response ({(int)resp.StatusCode}).");
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                string code = err.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                doc.Dispose();
                throw new RankException(FriendlyError(code, subject));
            }
            if (!resp.IsSuccessStatusCode)
            {
                doc.Dispose();
                throw new RankException($"maple-kit.com request failed ({(int)resp.StatusCode}).");
            }
            return doc;
        }
    }

    /// <summary>Map a Nexon <c>OPENAPI000xx</c> error code (passed through by the proxy) to a short,
    /// user-facing line. There's no key on our side, so the key-mismatch codes can't occur here.</summary>
    private static string FriendlyError(string code, string subject) => code switch
    {
        // A non-existent character name comes back as OPENAPI00004 (invalid parameter), same as a bad ocid.
        "OPENAPI00003" or "OPENAPI00004" => string.IsNullOrEmpty(subject)
            ? "Character not found." : $"Character \"{subject}\" not found.",
        "OPENAPI00007" => "Hit the MapleStory API rate limit — try again in a moment.",
        "OPENAPI00009" => "MapleStory data is still being prepared — try again later.",
        "OPENAPI00010" or "OPENAPI00011" => "The MapleStory API is under maintenance.",
        _ => string.IsNullOrEmpty(code) ? "maple-kit.com returned an error." : $"maple-kit.com error ({code}).",
    };

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? ShortDate(string? iso) => iso is { Length: >= 10 } ? iso[..10] : null;

    /// <summary>"12.830" → "12.83"; leaves a non-numeric value as-is.</summary>
    private static string FormatExp(string? rate)
        => double.TryParse(rate, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.##", CultureInfo.InvariantCulture)
            : (rate ?? "0");
}
