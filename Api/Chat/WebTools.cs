using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace TypePet.Api.Chat;

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
    private const int EmbeddedCharCap = 200_000;
    private const int ExcerptRadius = 800;
    private const int MaxMatchesPerToken = 24;
    private const int MaxWindowChars = 2400;

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
        "Fetch a web page and return its readable text, including data embedded by JavaScript apps. Use " +
        "after web_search to read a result in full. For long pages, pass `query` keywords to get the " +
        "sections that mention them instead of just the beginning of the page.",
        new Dictionary<string, JsonElement>
        {
            ["url"] = ToolSchema.String("The absolute URL to fetch."),
            ["query"] = ToolSchema.String("Optional keywords (the fact you're after, e.g. a skill or item name); " +
                                          "on a long page the result becomes the sections matching them."),
        },
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
        => FetchReadableAsync(Arg(argumentsJson, "url"), Arg(argumentsJson, "query"), ct);

    /// <summary>Tool-free overload (prompt-command RAG): no focus query, and a neutral truncation marker —
    /// the receiving model has no tools, so "call again with `query`" would be an un-followable hint.</summary>
    public static Task<string> FetchReadableAsync(string url, CancellationToken ct)
        => FetchCoreAsync(url, "", toolHint: false, ct);

    public static Task<string> FetchReadableAsync(string url, string focus, CancellationToken ct)
        => FetchCoreAsync(url, focus, toolHint: true, ct);

    /// <summary>True when a <see cref="FetchReadableAsync"/> result reports that no page was read (bad URL
    /// or transport failure) rather than page content. Lets callers avoid citing a source that was never
    /// actually fetched.</summary>
    public static bool IsFetchError(string result) =>
        result.StartsWith("Fetch failed:", StringComparison.Ordinal) ||
        result.StartsWith("Invalid or missing URL.", StringComparison.Ordinal);

    /// <summary>Download a page and return its readable text — "Title: …", "Summary: …" (meta description),
    /// then the body plus any JSON data the page embeds for a JavaScript app (where sites like Grandis
    /// Library keep ALL the real content), capped. A non-empty <paramref name="focus"/> turns the cap into
    /// relevance selection: the sections matching the focus words are returned instead of the page's head.
    /// Static and keyless so the knowledge base (<see cref="KnowledgeBase"/>) reuses the exact same fetch
    /// as the <c>web_fetch</c> tool. Best-effort: any failure returns a short message.</summary>
    private static async Task<string> FetchCoreAsync(string url, string focus, bool toolHint, CancellationToken ct)
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

            // SPA pages (Next.js and friends) ship their REAL content as JSON inside a script tag that the
            // text extraction below throws away — harvest it first so skill tables, stats, and prices survive.
            string embedded = ExtractEmbeddedData(doc);

            // Plain text drops <a> hrefs, so a link like "Class Discord" loses its real invite URL and the
            // model guesses one. Fold the target into the text ("Class Discord (https://discord.gg/…)") first.
            AnnotateLinks(doc, uri);

            foreach (var el in doc.QuerySelectorAll("script, style, noscript, nav, footer, header, aside, form, svg"))
                el.Remove();
            string body = Collapse(doc.Body?.TextContent ?? "");

            string content = embedded.Length == 0 ? body
                : body.Length == 0 ? "Embedded page data:\n" + embedded
                : body + "\nEmbedded page data:\n" + embedded;

            var sb = new StringBuilder();
            if (title.Length > 0) sb.AppendLine("Title: " + title);
            if (desc.Length > 0) sb.AppendLine("Summary: " + desc);
            if (content.Length > 0) sb.AppendLine(Clip(content, focus, toolHint));

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

    /// <summary>Cap long content: with a focus, return the sections matching it; without one, head-truncate
    /// (hinting that re-fetching with `query` pulls specific sections, when the caller has tools).</summary>
    private static string Clip(string content, string focus, bool toolHint)
    {
        if (content.Length <= FetchCharCap) return content;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            string picked = SelectRelevant(content, focus, FetchCharCap);
            if (picked.Length > 0)
                return $"(long page — showing the sections matching \"{Collapse(focus)}\")\n" + picked;
            return content[..FetchCharCap] + $" …[truncated — nothing on the page matched \"{Collapse(focus)}\"; try different `query` keywords]";
        }
        return content[..FetchCharCap] + (toolHint
            ? " …[truncated — call again with `query` keywords to pull the sections you need]"
            : " …[truncated]");
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

    // ---- embedded JSON data --------------------------------------------------------

    /// <summary>Harvest the JSON state SPA frameworks embed in the page (Next.js's
    /// <c>__NEXT_DATA__</c>, SvelteKit data scripts, schema.org <c>ld+json</c>) as readable text. For
    /// data-driven sites this is where the actual content lives — the rendered HTML is just a shell.</summary>
    private static string ExtractEmbeddedData(IDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var s in doc.QuerySelectorAll("script[type='application/json'], script[type='application/ld+json']"))
        {
            if (sb.Length >= EmbeddedCharCap) break;
            string json = s.TextContent;
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var d = JsonDocument.Parse(json);
                FlattenJson(d.RootElement, sb);
            }
            catch { /* not valid JSON — skip */ }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Flatten JSON to text, one line per object holding its primitive fields ("name: X; type: Origin
    /// Skill") so related values stay together for excerpt selection. URL/path-like and empty values are
    /// dropped as noise (icons, asset paths).</summary>
    private static void FlattenJson(JsonElement el, StringBuilder sb)
    {
        if (sb.Length >= EmbeddedCharCap) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var line = new StringBuilder();
                foreach (var p in el.EnumerateObject())
                {
                    string v = PrimitiveText(p.Value);
                    if (v.Length == 0) continue;
                    if (line.Length > 0) line.Append("; ");
                    line.Append(p.Name).Append(": ").Append(v);
                }
                if (line.Length > 0) sb.AppendLine(line.ToString());
                foreach (var p in el.EnumerateObject())
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        FlattenJson(p.Value, sb);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) FlattenJson(item, sb);
                break;
            default:
                string s = PrimitiveText(el);
                if (s.Length > 0) sb.AppendLine(s);
                break;
        }
    }

    private static string PrimitiveText(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                string s = Collapse(el.GetString() ?? "");
                if (s.Length == 0 || LooksLikeUrlOrPath(s)) return "";
                return s.Length <= 1000 ? s : s[..1000] + "…";
            case JsonValueKind.Number: return el.GetRawText();
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            default: return "";
        }
    }

    private static bool LooksLikeUrlOrPath(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("//", StringComparison.Ordinal) ||
        (s.StartsWith("/", StringComparison.Ordinal) && !s.Contains(' '));

    // ---- relevance selection -------------------------------------------------------

    /// <summary>Pick the windows of <paramref name="text"/> around matches of <paramref name="focus"/> —
    /// the whole phrase plus its words — merged, ranked (windows matching more distinct tokens first, so
    /// "Origin Skill" sections beat generic "skill" mentions), and joined in document order up to
    /// <paramref name="cap"/> chars. Windows stop at line boundaries so one flattened JSON object stays
    /// intact without dragging in its neighbors. Empty when nothing matches.</summary>
    private static string SelectRelevant(string text, string focus, int cap)
    {
        // Tokens: the full phrase first (most specific), then words. Short Latin words are noise; short
        // CJK tokens are real words and kept.
        var tokens = new List<string>();
        string phrase = Collapse(focus);
        if (phrase.Length >= 2) tokens.Add(phrase);
        foreach (var w in Regex.Split(phrase, @"[^\p{L}\p{N}]+"))
        {
            if (w.Length == 0) continue;
            bool cjk = false;
            foreach (var ch in w) if (ch >= 0x2E80) { cjk = true; break; }
            if ((w.Length >= 3 || cjk) && !tokens.Contains(w, StringComparer.OrdinalIgnoreCase))
                tokens.Add(w);
        }

        // Every match position of every token.
        var marks = new List<(int pos, int len, string tok)>();
        foreach (var t in tokens)
        {
            int from = 0, found = 0;
            while (found < MaxMatchesPerToken &&
                   (from = text.IndexOf(t, from, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                marks.Add((from, t.Length, t));
                from += t.Length;
                found++;
            }
        }
        if (marks.Count == 0) return "";
        marks.Sort((a, b) => a.pos.CompareTo(b.pos));

        // Expand each match to a window (bounded by its line) and merge overlaps, tracking which tokens
        // each merged window covers. Growth is capped: on a dense single-line body the matches would
        // otherwise chain-merge into one window bigger than the whole budget.
        var windows = new List<(int start, int end, HashSet<string> toks)>();
        foreach (var (pos, len, tok) in marks)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
            int lineEnd = text.IndexOf('\n', pos + len);
            if (lineEnd < 0) lineEnd = text.Length;
            int ws = Math.Max(lineStart, pos - ExcerptRadius);
            int we = Math.Min(lineEnd, pos + len + ExcerptRadius);

            if (windows.Count > 0 && ws <= windows[^1].end &&
                Math.Max(windows[^1].end, we) - windows[^1].start <= MaxWindowChars)
            {
                var last = windows[^1];
                last.toks.Add(tok);
                windows[^1] = (last.start, Math.Max(last.end, we), last.toks);
            }
            else
            {
                // Start past the previous window so refusing a merge can't emit the same text twice.
                int start = windows.Count > 0 ? Math.Max(ws, windows[^1].end) : ws;
                if (start >= we) continue; // fully inside the previous window already
                windows.Add((start, we, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tok }));
            }
        }

        // Greedily keep the most specific windows — exact-phrase windows beat any combination of single
        // generic words — then emit the kept ones in document order. Window size never exceeds
        // MaxWindowChars < cap, so something is always kept when there was a match.
        var kept = new List<(int start, int end)>();
        int total = 0;
        foreach (var w in windows.OrderByDescending(w => (w.toks.Contains(phrase) ? tokens.Count : 0) + w.toks.Count)
                                 .ThenBy(w => w.start))
        {
            int size = w.end - w.start;
            if (total + size > cap) continue;
            kept.Add((w.start, w.end));
            total += size;
        }
        kept.Sort((a, b) => a.start.CompareTo(b.start));

        var sb = new StringBuilder();
        foreach (var (s, e) in kept)
        {
            if (sb.Length > 0) sb.Append("\n…\n");
            sb.Append(text[s..e].Trim());
        }
        return sb.ToString();
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
