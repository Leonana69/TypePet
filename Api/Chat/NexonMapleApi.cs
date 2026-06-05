using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>Raised when the Nexon Open API rejects a request. <see cref="Exception.Message"/> is
/// user-facing (shown in the pet's bubble), so keep it short and plain.</summary>
public sealed class NexonApiException : Exception
{
    public NexonApiException(string message) : base(message) { }
}

/// <summary>
/// A tiny, deterministic (NO-LLM) client for the Nexon Open API used by the <c>/rank</c> chat command.
/// It looks a character up by name to get its <c>ocid</c>, then reads the character's basic info plus
/// best-effort popularity and union (Legion). Auth is the user's key in the <c>x-nxopen-api-key</c>
/// header.
///
/// The Nexon Open API exposes the same endpoints for several MapleStory <see cref="Region"/>s under
/// different path prefixes (<c>/maplestory</c> = Korea, <c>/maplestorysea</c> = SEA, <c>/maplestorytw</c>
/// = Taiwan). KEYS ARE PER-REGION: a key issued for one region can't query another (the gateway then
/// returns <c>OPENAPI00006</c>). So the key is stored per region under <see cref="SecretId(string)"/>.
/// Get a key at https://openapi.nexon.com.
/// </summary>
public sealed class NexonMapleApi
{
    /// <summary>A queryable MapleStory region: a stable <see cref="Id"/> (persisted + used in the secret
    /// id), a human <see cref="Label"/>, and the API <see cref="BasePath"/> its endpoints live under.
    /// <see cref="RequiresKey"/> is false for GMS, which has no Open API and uses the keyless public rankings
    /// endpoint (<see cref="NexonGmsRankApi"/>) instead. <see cref="InfoSite"/> + <see cref="InfoUrlFormat"/>
    /// give that region's community profile site for the <c>/rank</c> "check more info on …" link
    /// (<see cref="InfoUrlFormat"/> has a single <c>{0}</c> for the URL-encoded character name).</summary>
    public sealed record Region(string Id, string Label, string BasePath, bool RequiresKey = true,
        string? InfoSite = null, string? InfoUrlFormat = null);

    /// <summary>The MapleStory servers <c>/rank</c> can query. The Open-API regions (KMS/SEA/TMS) each need
    /// their own key; GMS is keyless and is the default. GMS is one entry — its NA/EU split is chosen per
    /// call via the <c>/rank -na|-eu</c> flag (see <see cref="NexonGmsRankApi"/>).</summary>
    public static readonly IReadOnlyList<Region> Regions = new[]
    {
        new Region("kms", "KMS (Korea)",  "https://open.api.nexon.com/maplestory/v1",
            InfoSite: "chuchu.gg",     InfoUrlFormat: "https://chuchu.gg/char/{0}"),
        new Region("sea", "SEA",          "https://open.api.nexon.com/maplestorysea/v1",
            InfoSite: "maple.gg",      InfoUrlFormat: "https://msea.maple.gg/u/{0}"),
        new Region("tms", "TMS (Taiwan)", "https://open.api.nexon.com/maplestorytw/v1",
            InfoSite: "maple-kit.com", InfoUrlFormat: "https://maple-kit.com/character/{0}"),
        // GMS has no Open API — this hits the public website ranking endpoint (no key). The trailing /na|/eu
        // sub-server is appended per call by NexonGmsRankApi based on the /rank flag, so BasePath omits it.
        new Region("gms", "GMS", "https://www.nexon.com/api/maplestory/no-auth/ranking/v2", RequiresKey: false,
            InfoSite: "MapleRanks",    InfoUrlFormat: "https://mapleranks.com/u/{0}"),
    };

    /// <summary>Resolve a persisted region id to its <see cref="Region"/> (falls back to the first/KMS).</summary>
    public static Region ResolveRegion(string? id)
        => Regions.FirstOrDefault(r => r.Id == id) ?? Regions[0];

    /// <summary>The <c>ISecretStore</c> id holding the Nexon key for <paramref name="regionId"/> — keys
    /// are per region, so each is stored separately (e.g. <c>nexon-kms</c>, <c>nexon-sea</c>).</summary>
    public static string SecretId(string regionId) => $"nexon-{regionId}";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return c;
    }

    /// <summary>The fields <c>/rank</c> shows. Optional fields are null when absent or a best-effort
    /// sub-request failed (the core character info still returns).</summary>
    public sealed record CharacterRank(
        string Name, string World, string Class, int Level, string ExpRate,
        string? Guild, string? CreatedDate, string? ImageUrl,
        int? Popularity, int? UnionLevel, string? UnionGrade);

    /// <summary>Look <paramref name="characterName"/> up in <paramref name="regionId"/>. Throws
    /// <see cref="NexonApiException"/> with a user-facing message on any API failure (bad/region-mismatched
    /// key, unknown character, rate limit, …).</summary>
    public async Task<CharacterRank> GetRankAsync(string characterName, string apiKey, string regionId, CancellationToken ct)
    {
        string b = ResolveRegion(regionId).BasePath;

        using var idDoc = await GetAsync(b, $"/id?character_name={Uri.EscapeDataString(characterName)}", apiKey, characterName, ct);
        if (!idDoc.RootElement.TryGetProperty("ocid", out var oc) || oc.GetString() is not { Length: > 0 } ocid)
            throw new NexonApiException("Unexpected response from the MapleStory API.");

        using var basicDoc = await GetAsync(b, $"/character/basic?ocid={ocid}", apiKey, characterName, ct);
        var bd = basicDoc.RootElement;
        string name = Str(bd, "character_name") ?? characterName;
        string world = Str(bd, "world_name") ?? "?";
        string cls = Str(bd, "character_class") ?? "?";
        int level = bd.TryGetProperty("character_level", out var lv) && lv.TryGetInt32(out var l) ? l : 0;
        string expRate = FormatExp(Str(bd, "character_exp_rate"));
        string? guild = NullIfEmpty(Str(bd, "character_guild_name"));
        string? created = ShortDate(Str(bd, "character_date_create"));
        string? image = NullIfEmpty(Str(bd, "character_image"));

        // Best-effort extras: a failure here (rate limit, region missing the endpoint) must NOT sink the
        // core lookup.
        int? popularity = await TryIntAsync(b, $"/character/popularity?ocid={ocid}", "popularity", apiKey, ct);
        var (unionLevel, unionGrade) = await TryUnionAsync(b, ocid, apiKey, ct);

        return new CharacterRank(name, world, cls, level, expRate, guild, created, image, popularity, unionLevel, unionGrade);
    }

    private async Task<int?> TryIntAsync(string baseUrl, string path, string field, string apiKey, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync(baseUrl, path, apiKey, "", ct);
            return doc.RootElement.TryGetProperty(field, out var v) && v.TryGetInt32(out var n) ? n : null;
        }
        catch { return null; }
    }

    private async Task<(int? level, string? grade)> TryUnionAsync(string baseUrl, string ocid, string apiKey, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync(baseUrl, $"/user/union?ocid={ocid}", apiKey, "", ct);
            var u = doc.RootElement;
            int? level = u.TryGetProperty("union_level", out var lv) && lv.TryGetInt32(out var l) ? l : null;
            return (level, NullIfEmpty(Str(u, "union_grade")));
        }
        catch { return (null, null); }
    }

    /// <summary>GET <paramref name="baseUrl"/> + <paramref name="path"/> and return the parsed JSON (the
    /// caller disposes it). Nexon signals failures with an <c>{"error":{"name","message"}}</c> envelope
    /// (usually HTTP 400); those — and transport failures — are mapped to a <see cref="NexonApiException"/>
    /// with a friendly message (<paramref name="subject"/> names the character for "not found").</summary>
    private static async Task<JsonDocument> GetAsync(string baseUrl, string path, string apiKey, string subject, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        req.Headers.TryAddWithoutValidation("x-nxopen-api-key", apiKey);

        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { throw new NexonApiException("The MapleStory API timed out — try again."); }
        catch (HttpRequestException ex) { throw new NexonApiException($"Couldn't reach the MapleStory API ({ex.Message})."); }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
            catch { throw new NexonApiException($"The MapleStory API returned an unreadable response ({(int)resp.StatusCode})."); }

            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                string code = err.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                doc.Dispose();
                throw new NexonApiException(FriendlyError(code, subject));
            }
            if (!resp.IsSuccessStatusCode)
            {
                doc.Dispose();
                throw new NexonApiException($"The MapleStory API request failed ({(int)resp.StatusCode}).");
            }
            return doc;
        }
    }

    /// <summary>Map a Nexon <c>OPENAPI000xx</c> error code to a short, user-facing line.</summary>
    private static string FriendlyError(string code, string subject) => code switch
    {
        // A non-existent character name comes back as OPENAPI00004 (invalid parameter), same as a bad ocid.
        "OPENAPI00003" or "OPENAPI00004" => string.IsNullOrEmpty(subject)
            ? "Character not found." : $"Character \"{subject}\" not found on this server.",
        "OPENAPI00005" => "Invalid Nexon API key — check it in Settings.",
        "OPENAPI00002" => "Nexon API key not authorized — check it in Settings.",
        // The gateway returns this when the key's region doesn't match the path (e.g. a KMS key on SEA).
        "OPENAPI00006" => "This key doesn't match the selected server — keys are per region (KMS/SEA/TMS). Check Settings.",
        "OPENAPI00007" => "Hit the Nexon API rate limit — try again in a moment.",
        "OPENAPI00009" => "MapleStory data is still being prepared — try again later.",
        "OPENAPI00010" or "OPENAPI00011" => "The MapleStory API is under maintenance.",
        _ => string.IsNullOrEmpty(code) ? "The MapleStory API returned an error." : $"MapleStory API error ({code}).",
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
