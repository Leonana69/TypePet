using System;
using System.Linq;
using System.Threading;

namespace MaplePet.Api.Chat;

/// <summary>
/// Dev-only smoke test for the MapleStory knowledge base (RAG). Loads the bundled catalog, checks
/// language detection, and prints the language-prioritized ranking + the directory the model would see
/// for a few queries (or a query passed on the command line). Handy when adding sources to
/// <c>Assets/Program/Knowledge/sources.json</c> — run <c>--rag-test "이너어빌리티 히어로"</c> to see which
/// source/URL the chatbot would be handed. Invoked from <see cref="Program"/>; exits without starting the UI.
/// </summary>
public static class RagTest
{
    public static int Run(string? query)
    {
        // The catalog and queries are full of Hangul/CJK; force UTF-8 so the console doesn't mangle them.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* output may be redirected */ }

        var kb = KnowledgeBase.LoadBundled();
        Console.WriteLine($"Loaded {kb.Sources.Count} knowledge source(s) from the bundled catalog.");
        if (kb.Sources.Count == 0)
        {
            Console.WriteLine("FAIL: no sources loaded — check Assets/Program/Knowledge/sources.json and the AvaloniaResource glob.");
            return 1;
        }
        foreach (var s in kb.Sources)
            Console.WriteLine($"  - {s.Id}: {s.Name} [{string.Join(",", s.Languages)}] · {s.Templates.Count} template(s)");
        Console.WriteLine();

        // Language detection sanity checks.
        Console.WriteLine("Language detection:");
        foreach (var (text, expect) in new[]
                 {
                     ("히어로 이너어빌리티 알려줘", "ko"),
                     ("what is the hero hyper skill build", "en"),
                     ("ヒーローのスキル", "ja"),
                     ("英雄技能", "zh"),
                 })
        {
            string got = KnowledgeBase.DetectLanguage(text);
            Console.WriteLine($"  [{(got == expect ? "ok" : "??")}] \"{text}\" → {got} (expected {expect})");
        }
        Console.WriteLine();

        var queries = query is { Length: > 0 }
            ? new[] { query }
            : new[] { "히어로 이너어빌리티 하이퍼 링크스킬", "best hero hyper skill build" };

        foreach (var q in queries)
        {
            string lang = KnowledgeBase.DetectLanguage(q);
            Console.WriteLine($"Query: \"{q}\"  (lang={lang})");
            var ranked = kb.Rank(q);
            Console.WriteLine("  ranked: " + string.Join(" > ", ranked.Select(s => $"{s.Name}[{string.Join(",", s.Languages)}]")));
            Console.WriteLine("  --- directory the model sees ---");
            foreach (var line in kb.Directory(ranked).TrimEnd().Split('\n'))
                Console.WriteLine("  " + line.TrimEnd());
            Console.WriteLine();
        }

        // Exercise the real fetch path against the top source's first example URL (best-effort; needs network).
        var top = kb.Rank(queries[0]).First();
        var sample = top.Templates.SelectMany(t => t.Examples).FirstOrDefault();
        if (sample is not null)
        {
            Console.WriteLine($"Fetching sample URL via maple_lookup: {sample}");
            try
            {
                var argsJson = System.Text.Json.JsonSerializer.Serialize(new { url = sample });
                var (text, sources) = kb.RunLookupAsync(argsJson, CancellationToken.None).GetAwaiter().GetResult();
                string head = text.Length <= 200 ? text : text[..200] + " …";
                Console.WriteLine($"  got {text.Length} chars, {sources.Count} source(s). Head: {head.Replace('\n', ' ')}");
            }
            catch (Exception ex) { Console.WriteLine($"  (fetch skipped/failed: {ex.Message})"); }
        }

        return 0;
    }
}
