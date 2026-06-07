using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MaplePet.Api.Chat;

/// <summary>A web result surfaced to the chat bar's "Sources" list.</summary>
public sealed record WebSource(string Title, string Url, string Snippet);

/// <summary>
/// Keyless web tools offered to EVERY provider so search works identically for Claude, GPT, DeepSeek,
/// and local models — no third-party search account. <c>web_search</c> scrapes DuckDuckGo's HTML
/// endpoint; <c>web_fetch</c> downloads a page and extracts readable text (so the model can open a
/// result to answer weather/news/prices accurately). HTML is parsed with AngleSharp. Best-effort: every
/// failure returns a short message instead of throwing.
/// </summary>
public sealed class WebTools
{
    public const string SearchToolName = "web_search";
    public const string FetchToolName = "web_fetch";

    private const int MaxResults = 5;
    private const int FetchCharCap = 6000;
    private const int MaxDownloadBytes = 3_000_000;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly HtmlParser Parser = new();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // A normal browser UA so DuckDuckGo / sites return the standard HTML.
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return c;
    }

    public bool Handles(string name) => name == SearchToolName || name == FetchToolName;

    public ChatToolDef SearchDefinition => new(
        SearchToolName,
        "Search the web (DuckDuckGo) for current or external information. Returns titled results with URLs and snippets. For exact facts (weather, prices, news), follow up with web_fetch on the best result.",
        new Dictionary<string, JsonElement> { ["query"] = ToolSchema.String("The search query.") },
        new[] { "query" });

    public ChatToolDef FetchDefinition => new(
        FetchToolName,
        "Fetch a web page and return its readable text. Use after web_search to read a result in full.",
        new Dictionary<string, JsonElement> { ["url"] = ToolSchema.String("The absolute URL to fetch.") },
        new[] { "url" });

    // ---- web_search --------------------------------------------------------------

    public async Task<(string text, IReadOnlyList<WebSource> sources)> RunSearchAsync(string argumentsJson, CancellationToken ct)
    {
        string query = Arg(argumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query))
            return ("No query provided.", Array.Empty<WebSource>());

        try
        {
            var sources = await DuckDuckGoAsync(query, ct).ConfigureAwait(false);
            if (sources.Count == 0)
                return ($"No results for \"{query}\".", sources);
            return (FormatResults(sources), sources);
        }
        catch (Exception ex)
        {
            return ($"Web search failed: {ex.Message}", Array.Empty<WebSource>());
        }
    }

    /// <summary>Run a search and return just the formatted result block — the same text
    /// <see cref="RunSearchAsync"/> produces, but keyless/static so prompt-command RAG
    /// (<see cref="PromptRag"/>) can splice search results into a prompt without a tool call.
    /// Best-effort: any failure returns a short message instead of throwing.</summary>
    public static async Task<string> SearchReadableAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return "No query provided.";
        try
        {
            var sources = await DuckDuckGoAsync(query, ct).ConfigureAwait(false);
            return sources.Count == 0 ? $"No results for \"{query}\"." : FormatResults(sources);
        }
        catch (Exception ex) { return $"Web search failed: {ex.Message}"; }
    }

    /// <summary>Format DuckDuckGo results as a numbered "[n] Title — Url\nSnippet" block.</summary>
    private static string FormatResults(IReadOnlyList<WebSource> sources)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            sb.AppendLine($"[{i + 1}] {s.Title} — {s.Url}");
            if (!string.IsNullOrWhiteSpace(s.Snippet)) sb.AppendLine(s.Snippet);
        }
        return sb.ToString().TrimEnd();
    }

    private static async Task<IReadOnlyList<WebSource>> DuckDuckGoAsync(string query, CancellationToken ct)
    {
        // The html.duckduckgo.com endpoint serves a plain, parseable result list and accepts a POST form.
        using var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query) });
        using var resp = await Http.PostAsync("https://html.duckduckgo.com/html/", form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = Parser.ParseDocument(html);
        var results = new List<WebSource>();
        foreach (var node in doc.QuerySelectorAll(".result, .web-result"))
        {
            var a = node.QuerySelector("a.result__a");
            if (a is null) continue;
            string title = Collapse(a.TextContent);
            string url = CleanDdgUrl(a.GetAttribute("href") ?? "");
            string snippet = Collapse(node.QuerySelector(".result__snippet")?.TextContent ?? "");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            results.Add(new WebSource(title, url, snippet));
            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    /// <summary>DuckDuckGo wraps result links as <c>//duckduckgo.com/l/?uddg=&lt;encoded&gt;</c>; unwrap to
    /// the real target. Also normalize protocol-relative hrefs.</summary>
    private static string CleanDdgUrl(string href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        int i = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (i >= 0)
        {
            int start = i + 5;
            int amp = href.IndexOf('&', start);
            string enc = amp >= 0 ? href[start..amp] : href[start..];
            try { return Uri.UnescapeDataString(enc); } catch { /* fall through */ }
        }
        if (href.StartsWith("//", StringComparison.Ordinal)) return "https:" + href;
        return href;
    }

    // ---- web_fetch ---------------------------------------------------------------

    public Task<string> RunFetchAsync(string argumentsJson, CancellationToken ct)
        => FetchReadableAsync(Arg(argumentsJson, "url"), ct);

    /// <summary>Download a page and return its readable text — "Title: …", "Summary: …" (meta description),
    /// then the body, capped. Static and keyless so the knowledge base (<see cref="KnowledgeBase"/>) reuses
    /// the exact same fetch as the <c>web_fetch</c> tool. Best-effort: any failure returns a short message.</summary>
    public static async Task<string> FetchReadableAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Invalid or missing URL.";

        try
        {
            using var resp = await Http.GetAsync(uri, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string html = await ReadCappedAsync(resp.Content, ct).ConfigureAwait(false);

            using var doc = Parser.ParseDocument(html);

            // Capture the title + meta description BEFORE stripping — for JavaScript-heavy pages (weather
            // and news apps) these are often the only text present, and frequently carry the answer.
            string title = Collapse(doc.Title ?? "");
            string desc = MetaDescription(doc);

            // Plain text drops <a> hrefs, so a link like "Class Discord" loses its real invite URL and the
            // model guesses one. Fold the target into the text ("Class Discord (https://discord.gg/…)") first.
            AnnotateLinks(doc, uri);

            foreach (var el in doc.QuerySelectorAll("script, style, noscript, nav, footer, header, aside, form, svg"))
                el.Remove();
            string body = Collapse(doc.Body?.TextContent ?? "");

            var sb = new StringBuilder();
            if (title.Length > 0) sb.AppendLine("Title: " + title);
            if (desc.Length > 0) sb.AppendLine("Summary: " + desc);
            if (body.Length > 0) sb.AppendLine(body.Length <= FetchCharCap ? body : body[..FetchCharCap] + " …[truncated]");

            var outp = sb.ToString().Trim();
            return outp.Length > 0
                ? outp
                : "(this page returned no readable text — likely a JavaScript app. Use the web_search snippets, or try another result.)";
        }
        catch (Exception ex)
        {
            return $"Fetch failed: {ex.Message}";
        }
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length >= MaxDownloadBytes) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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

    private static string MetaDescription(IDocument doc)
    {
        var m = doc.QuerySelector("meta[name='description']") ?? doc.QuerySelector("meta[property='og:description']");
        return Collapse(m?.GetAttribute("content") ?? "");
    }

    /// <summary>Fold each EXTERNAL link's target into its anchor text as "text (href)", so the plain-text
    /// extraction keeps real URLs (Discord invites, wikis, videos) instead of just the words. Internal
    /// same-site links are left alone — they're navigational noise and the model can construct those itself.
    /// Absolute http(s) only; each target annotated once; capped so a link farm can't flood the output.</summary>
    private static void AnnotateLinks(IDocument doc, Uri pageUri)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int annotated = 0;
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            if (annotated >= 40) break;
            if (!Uri.TryCreate(a.GetAttribute("href"), UriKind.Absolute, out var lu)) continue;
            if (lu.Scheme != Uri.UriSchemeHttp && lu.Scheme != Uri.UriSchemeHttps) continue;
            if (SameHost(lu.Host, pageUri.Host)) continue;                            // skip internal links
            string text = Collapse(a.TextContent);
            if (text.Length == 0) continue;                                           // image-only / empty
            if (text.Contains(lu.Host, StringComparison.OrdinalIgnoreCase)) continue; // text already shows the URL
            if (!seen.Add(lu.AbsoluteUri)) continue;                                  // annotate each target once
            a.TextContent = $"{text} ({lu.AbsoluteUri})";
            annotated++;
        }
    }

    private static bool SameHost(string a, string b)
    {
        static string N(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();
}
