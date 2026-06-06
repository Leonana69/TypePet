using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace MaplePet.Api.Chat;

/// <summary>
/// One curated reference site the chatbot may read to answer MapleStory questions, loaded from the
/// bundled <c>Assets/Program/Knowledge/sources.json</c> catalog. Grow the knowledge base by adding
/// entries to that file — no code change needed. <see cref="Languages"/> drives language-prioritized
/// selection: a Korean question prefers a source whose languages include <c>ko</c>.
/// </summary>
public sealed class KnowledgeSource
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>ISO-639-1 codes this site is written in, e.g. <c>["ko"]</c>. A match against the
    /// question's detected language ranks this source first.</summary>
    public List<string> Languages { get; init; } = new();

    /// <summary>Optional MapleStory region tag (KMS/GMS/MSEA/TMS) — informational.</summary>
    public string? Region { get; init; }
    public string? Homepage { get; init; }

    /// <summary>One line: what this site covers and when to use it.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Free-form keywords/tags (any language) used to rank the source against a query.</summary>
    public List<string> Topics { get; init; } = new();

    /// <summary>URL templates for the site's readable pages. A template may contain <c>{placeholder}</c>
    /// slots the model fills (e.g. a job slug) before fetching.</summary>
    public List<KnowledgeTemplate> Templates { get; init; } = new();

    /// <summary>Optional guidance for the model (e.g. how to map a class name to a URL slug).</summary>
    public string? Notes { get; init; }
}

/// <summary>A URL pattern for one kind of page on a <see cref="KnowledgeSource"/>.</summary>
public sealed class KnowledgeTemplate
{
    public string UrlTemplate { get; init; } = "";
    public string? Description { get; init; }

    /// <summary>A few fully-resolved example URLs, shown to the model as few-shot guidance.</summary>
    public List<string> Examples { get; init; } = new();
}

/// <summary>
/// The chatbot's lightweight RAG layer: a catalog of authoritative MapleStory sites plus the
/// <c>maple_lookup</c> tool the model uses to retrieve from them. Selection is language-prioritized —
/// the question's script is detected and a same-language source ranks first — so a Korean question is
/// answered from a Korean site. Retrieval is two-step and model-driven: call <c>maple_lookup</c> with a
/// <c>query</c> to get the ranked directory of sources and their URL templates, then call it again with
/// a concrete <c>url</c> (built from a template) to fetch and read that page. Keyless and provider-neutral
/// (offered to every backend), it reuses <see cref="WebTools.FetchReadableAsync"/> for the actual fetch.
/// </summary>
public sealed class KnowledgeBase
{
    public const string LookupToolName = "maple_lookup";

    private const string CatalogUri = "avares://MaplePet/Assets/Program/Knowledge/sources.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<KnowledgeSource> _sources;

    public KnowledgeBase(IEnumerable<KnowledgeSource> sources) => _sources = sources.ToList();

    public IReadOnlyList<KnowledgeSource> Sources => _sources;
    public bool HasSources => _sources.Count > 0;
    public bool Handles(string name) => name == LookupToolName;

    /// <summary>Load the bundled catalog. Never throws — a missing/invalid file yields an empty base.</summary>
    public static KnowledgeBase LoadBundled()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri(CatalogUri));
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }
        catch { return new KnowledgeBase(Array.Empty<KnowledgeSource>()); }
    }

    /// <summary>Parse a catalog JSON string into a base. Drops entries missing an id; never throws.</summary>
    public static KnowledgeBase Parse(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<Catalog>(json, JsonOpts);
            var list = (doc?.Sources ?? new())
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id))
                .ToList();
            return new KnowledgeBase(list);
        }
        catch { return new KnowledgeBase(Array.Empty<KnowledgeSource>()); }
    }

    private sealed class Catalog { public List<KnowledgeSource>? Sources { get; init; } }

    // ---- language detection ------------------------------------------------------

    /// <summary>Best-effort script detection → ISO-639-1 (<c>ko</c>/<c>ja</c>/<c>zh</c>/<c>en</c>). Hangul ⇒
    /// Korean; else kana ⇒ Japanese; else Han ⇒ Chinese; else English. Good enough to prioritize a
    /// same-language source.</summary>
    public static string DetectLanguage(string text)
    {
        bool kana = false, han = false;
        foreach (var ch in text ?? "")
        {
            if ((ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0x1100 && ch <= 0x11FF) || (ch >= 0x3130 && ch <= 0x318F))
                return "ko";                                     // Hangul syllables / Jamo ⇒ Korean
            if (ch >= 0x3040 && ch <= 0x30FF) kana = true;       // Hiragana + Katakana
            else if (ch >= 0x4E00 && ch <= 0x9FFF) han = true;   // CJK Unified Ideographs
        }
        if (kana) return "ja";
        if (han) return "zh";
        return "en";
    }

    // ---- selection ---------------------------------------------------------------

    /// <summary>Rank sources for a query: same-language sources first (the requested priority), then by
    /// topic/summary overlap. Stable for ties (original catalog order).</summary>
    public IReadOnlyList<KnowledgeSource> Rank(string query, string? language = null)
    {
        string lang = string.IsNullOrEmpty(language) ? DetectLanguage(query ?? "") : language!;
        string q = (query ?? "").ToLowerInvariant();
        return _sources
            .Select((s, i) => (s, i, score: Score(s, q, lang)))
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.i)
            .Select(t => t.s)
            .ToList();
    }

    private static int Score(KnowledgeSource s, string qLower, string lang)
    {
        int score = 0;
        if (s.Languages.Any(l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)))
            score += 1000;                                       // language match dominates — prioritized
        if (qLower.Length > 0)
        {
            foreach (var t in s.Topics)
                if (!string.IsNullOrWhiteSpace(t) && qLower.Contains(t.ToLowerInvariant()))
                    score += 5;
            foreach (var w in s.Summary.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 4 && qLower.Contains(w)) score += 1;
        }
        return score;
    }

    // ---- tool --------------------------------------------------------------------

    public ChatToolDef LookupDefinition => new(
        LookupToolName,
        "Look up authoritative MapleStory reference sites (class/skill guides: inner ability, hyper & link " +
        "skills, builds, cores, union, boss guides, etc.). A source written in the question's language is " +
        "preferred. Call with just `query` to get the ranked directory of matching sites and their URL " +
        "templates; then call again with a concrete `url` (built from a template, substituting the right " +
        "English slug) to fetch and read that page.",
        new Dictionary<string, JsonElement>
        {
            ["query"] = ToolSchema.String("What to look up (topic/keywords, in any language). Used to pick and rank sources."),
            ["url"] = ToolSchema.String("Optional: a fully-resolved page URL to fetch and read. Build it from a template shown in the directory."),
        },
        Array.Empty<string>());

    /// <summary>Run a <c>maple_lookup</c> call. With a <c>url</c> it fetches that page (the retrieval step)
    /// and returns its readable text + a cited source; with only a <c>query</c> it returns the
    /// language-ranked directory so the model can pick a source and build a URL.</summary>
    public async Task<(string text, IReadOnlyList<WebSource> sources)> RunLookupAsync(string argumentsJson, CancellationToken ct)
    {
        string query = Arg(argumentsJson, "query");
        string url = Arg(argumentsJson, "url");

        // A concrete URL → fetch and read the page.
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return ("Invalid url — pass an absolute http(s) URL built from one of the source templates.",
                        Array.Empty<WebSource>());

            string body = await WebTools.FetchReadableAsync(url, ct).ConfigureAwait(false);
            var match = _sources.FirstOrDefault(s => MatchesHost(s, uri));
            string title = ExtractTitle(body) ?? match?.Name ?? uri.Host;
            string header = match is not null ? $"From {match.Name} [{string.Join(",", match.Languages)}]:\n" : "";
            return (header + body, new[] { new WebSource(title, url, "") });
        }

        // No URL → return the ranked directory so the model can choose a source and build a URL.
        if (!HasSources)
            return ("No MapleStory knowledge sources are configured.", Array.Empty<WebSource>());

        string lang = DetectLanguage(query);
        var sb = new StringBuilder();
        sb.AppendLine($"Question language: {lang}. Sources are ranked below — a {lang} source is preferred. " +
                      "Pick one, build a concrete URL from its template (map class names to the English slug), " +
                      "then call maple_lookup again with that `url` to read the page.");
        sb.AppendLine();
        sb.Append(Directory(Rank(query, lang)));
        return (sb.ToString().TrimEnd(), Array.Empty<WebSource>());
    }

    // ---- rendering ---------------------------------------------------------------

    /// <summary>A compact, model-readable listing of the given sources (name, languages, region, summary,
    /// templates with examples, notes). Used by the <c>maple_lookup</c> directory response.</summary>
    public string Directory(IReadOnlyList<KnowledgeSource> sources)
    {
        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            sb.Append("• ").Append(s.Name);
            if (s.Languages.Count > 0) sb.Append(" [").Append(string.Join(",", s.Languages)).Append(']');
            if (!string.IsNullOrWhiteSpace(s.Region)) sb.Append(" (").Append(s.Region).Append(')');
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(s.Summary)) sb.Append("    ").AppendLine(s.Summary);
            foreach (var t in s.Templates)
            {
                sb.Append("    url: ").Append(t.UrlTemplate);
                if (!string.IsNullOrWhiteSpace(t.Description)) sb.Append("  — ").Append(t.Description);
                sb.AppendLine();
                if (t.Examples.Count > 0) sb.Append("      e.g. ").AppendLine(string.Join(", ", t.Examples));
            }
            if (!string.IsNullOrWhiteSpace(s.Notes)) sb.Append("    note: ").AppendLine(s.Notes);
        }
        return sb.ToString();
    }

    /// <summary>A one-line-per-source digest for the system prompt so the model knows these sources exist
    /// (details come on demand via <c>maple_lookup</c>).</summary>
    public string SystemPromptDigest()
    {
        var sb = new StringBuilder();
        foreach (var s in _sources)
        {
            sb.Append("- ").Append(s.Name);
            if (s.Languages.Count > 0) sb.Append(" [").Append(string.Join(",", s.Languages)).Append(']');
            if (!string.IsNullOrWhiteSpace(s.Summary)) sb.Append(": ").Append(s.Summary);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---- helpers -----------------------------------------------------------------

    private static string Arg(string json, string name)
    {
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return d.RootElement.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string? ExtractTitle(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        int nl = body.IndexOf('\n');
        string first = nl >= 0 ? body[..nl] : body;
        const string p = "Title: ";
        return first.StartsWith(p, StringComparison.Ordinal) ? first[p.Length..].Trim() : null;
    }

    /// <summary>True if <paramref name="uri"/>'s host matches the source's homepage or any template host
    /// (used only to label a fetched page with its source name; best-effort).</summary>
    private static bool MatchesHost(KnowledgeSource s, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(s.Homepage) && Uri.TryCreate(s.Homepage, UriKind.Absolute, out var h)
            && HostEq(h.Host, uri.Host)) return true;
        foreach (var t in s.Templates)
            if (Uri.TryCreate(StripPlaceholders(t.UrlTemplate), UriKind.Absolute, out var tu) && HostEq(tu.Host, uri.Host))
                return true;
        return false;
    }

    private static bool HostEq(string a, string b)
    {
        static string N(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripPlaceholders(string template) => Regex.Replace(template, @"\{[^}]*\}", "x");
}
