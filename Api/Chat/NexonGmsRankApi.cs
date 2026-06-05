using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaplePet.Api.Chat;

/// <summary>
/// A tiny, keyless client for Global MapleStory (GMS) rankings, used by <c>/rank</c> for the <c>-na</c> /
/// <c>-eu</c> flags (GMS North America / Europe). GMS is "very different" from the other servers: it has NO
/// Nexon <i>Open</i> API (the <c>open.api.nexon.com</c> ocid-lookup endpoints don't serve it), so there is
/// no API key to configure. Instead its rankings come straight from the public website's backing endpoint:
///
///   <c>GET https://www.nexon.com/api/maplestory/no-auth/ranking/v2/{na|eu}?type=overall&amp;id=weekly&amp;reboot_index=0&amp;character_name=…</c>
///
/// For a name lookup we query the Overall (weekly) leaderboard filtered by exact character name. That returns
/// just the matching row — including the character's <b>true global rank</b>, which (ironically) the Open-API
/// regions don't even expose. <c>reboot_index=0</c> = "Both", i.e. it spans both Interactive and Heroic
/// (reboot) worlds, so a single query finds the character whatever world they're on.
/// </summary>
public sealed class NexonGmsRankApi
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        // The endpoint is browser-facing; a normal UA avoids any naive bot filtering at the edge.
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MaplePet");
        return c;
    }

    /// <summary>worldID → world name, lifted from the rankings site bundle. Kronos (45) and Hyperion (70)
    /// are the Heroic (reboot) worlds; the rest are Interactive. Unknown ids fall back to "World N".</summary>
    private static readonly IReadOnlyDictionary<int, string> Worlds = new Dictionary<int, string>
    {
        [0] = "Scania", [1] = "Bera", [2] = "Broa", [3] = "Windia", [4] = "Khaini", [5] = "Bellocan",
        [6] = "Mardia", [7] = "Kradia", [8] = "Yellonde", [9] = "Demethos", [10] = "Galicia",
        [11] = "El Nido", [12] = "Zenith", [13] = "Arcania", [14] = "Chaos", [15] = "Nova",
        [16] = "Renegades", [17] = "Aurora", [18] = "Elysium", [19] = "Scania", [30] = "Luna",
        [45] = "Kronos", [46] = "Solis", [48] = "Challengers", [49] = "Challengers",
        [52] = "Challengers Heroic", [54] = "Challengers Heroic", [70] = "Hyperion",
    };

    /// <summary>The fields <c>/rank</c> shows for GMS. <see cref="Rank"/> is the character's global position
    /// in the Overall (weekly) ranking; <see cref="Total"/> is the size of that ranking — best-effort, null
    /// if the population sub-request failed. <see cref="LegionLevel"/> is 0 in the overall ranking.</summary>
    public sealed record GmsRank(
        string Name, long Rank, int Level, string Job, string World, string? ImageUrl, int LegionLevel, long? Total);

    /// <summary>Look <paramref name="characterName"/> up in the GMS Overall (weekly) ranking. The endpoint
    /// is <paramref name="baseUrl"/> (…/ranking/v2) plus the <paramref name="serverCode"/> sub-server
    /// (<c>na</c>/<c>eu</c>). Matching is exact, mirroring the site's own search. Throws
    /// <see cref="RankException"/> with a user-facing message on any failure or when the character isn't
    /// ranked.</summary>
    public async Task<GmsRank> GetRankAsync(string characterName, string baseUrl, string serverCode, CancellationToken ct)
    {
        string serverUrl = $"{baseUrl.TrimEnd('/')}/{serverCode}";

        // The character's row (with its global rank) and the ranking's population are independent, so fetch
        // both at once. The population call is best-effort and never throws — if the name lookup fails first,
        // it just completes (and is discarded) harmlessly.
        var rowTask = SearchAsync(serverUrl, characterName, ct);
        var totalTask = TryTotalAsync(serverUrl, ct);

        JsonElement row = await rowTask.ConfigureAwait(false); // may throw RankException (not found / API error)
        long? total = await totalTask.ConfigureAwait(false);

        long rank = row.TryGetProperty("rank", out var rk) && rk.TryGetInt64(out var rv) ? rv : 0;
        int level = row.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var l) ? l : 0;
        string job = Str(row, "jobName") ?? "?";
        int worldId = row.TryGetProperty("worldID", out var wd) && wd.TryGetInt32(out var wv) ? wv : -1;
        string world = Worlds.TryGetValue(worldId, out var wn) ? wn : (worldId >= 0 ? $"World {worldId}" : "?");
        string name = Str(row, "characterName") ?? characterName;
        string? image = NullIfEmpty(Str(row, "characterImgURL"));
        int legion = row.TryGetProperty("legionLevel", out var lg) && lg.TryGetInt32(out var lgv) ? lgv : 0;

        return new GmsRank(name, rank, level, job, world, image, legion, total);
    }

    /// <summary>The matched ranking row, detached (<see cref="JsonElement.Clone"/>) so it outlives the parsed
    /// document. Prefers the row the API flags as the search target; falls back to the first returned row.</summary>
    private static async Task<JsonElement> SearchAsync(string baseUrl, string name, CancellationToken ct)
    {
        string url = $"{baseUrl}?type=overall&id=weekly&reboot_index=0&character_name={Uri.EscapeDataString(name)}";
        using var doc = await GetAsync(url, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("ranks", out var ranks) ||
            ranks.ValueKind != JsonValueKind.Array || ranks.GetArrayLength() == 0)
            throw new RankException($"Character \"{name}\" not found in the GMS rankings.");

        foreach (var r in ranks.EnumerateArray())
            if (r.TryGetProperty("isSearchTarget", out var t) && t.ValueKind == JsonValueKind.True)
                return r.Clone();

        // No row was flagged as the search target. The endpoint is expected to match the name exactly, but
        // don't blindly trust that: only accept the first row if its name actually matches the query, so a
        // looser server-side match can never make us report a *different* character's rank under this name.
        var first = ranks[0];
        if (string.Equals(Str(first, "characterName")?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            return first.Clone();
        throw new RankException($"Character \"{name}\" not found in the GMS rankings.");
    }

    /// <summary>The total number of ranked characters (the leaderboard size, for "#N of T" context). Best
    /// effort: any failure returns null rather than sinking the lookup.</summary>
    private static async Task<long?> TryTotalAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            string url = $"{baseUrl}?type=overall&id=weekly&reboot_index=0&page_index=1";
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("totalCount", out var c) && c.TryGetInt64(out var n) ? n : null;
        }
        catch { return null; }
    }

    /// <summary>GET <paramref name="url"/> and return the parsed JSON (caller disposes). Transport failures
    /// and non-2xx responses become a <see cref="RankException"/> with a short, user-facing message.</summary>
    private static async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(url, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { throw new RankException("The GMS rankings API timed out — try again."); }
        catch (HttpRequestException ex) { throw new RankException($"Couldn't reach the GMS rankings API ({ex.Message})."); }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RankException($"The GMS rankings API request failed ({(int)resp.StatusCode}).");
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
            catch { throw new RankException("The GMS rankings API returned an unreadable response."); }
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
