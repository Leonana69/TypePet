using System;
using System.Linq;
using System.Threading;
using TypePet.Engine;

namespace TypePet.Api.Chat;

/// <summary>
/// Dev-only headless check for the user command library: loads the on-disk store, prints every command's
/// parse/validation result, builds the merged registry (built-ins + declarative), and optionally runs one
/// command and prints its <see cref="CommandResult"/>. No UI, no pet, no LLM — so clipboard/script/text
/// kinds run fully; prompt kinds report "no provider" (expected). Wired to <c>--commands-test [name]</c>.
/// </summary>
public static class CommandTest
{
    public static int Run(string? runName)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        bool reactionsOk = CheckReactionParsing();

        string root = CommandStore.ResolveDefaultRoot();
        Console.WriteLine($"Commands root: {root}");
        var store = new CommandStore(root);

        var entries = store.List();
        Console.WriteLine($"\nInstalled commands ({entries.Count}):");
        foreach (var e in entries)
            Console.WriteLine($"  {(e.Valid ? "OK " : "BAD")}  {e.Id,-16} /{e.Name,-12} [{e.Kind}]" +
                              (e.Valid ? "" : $"  -- {e.Error}"));

        // Build the merged registry the app uses (built-ins + enabled declarative commands). No pet/LLM.
        var commands = new ChatCommands(
            chatEnabled: () => false,
            buildConfig: () => null,
            pet: () => null,
            store: store,
            disabledIds: () => Array.Empty<string>(),
            scriptsEnabled: () => true,
            // Dev harness: trust every command's declared hosts: allowlist so kind:script network commands
            // (e.g. /rank) can be exercised headlessly, mirroring how it already force-enables scripts.
            networkApproved: (_, _) => true);

        Console.WriteLine($"\nMerged registry ({commands.Commands.Count}):");
        foreach (var c in commands.Commands)
            Console.WriteLine($"  {c.Usage,-26} {c.Help}");

        if (!string.IsNullOrWhiteSpace(runName))
        {
            string input = runName!.StartsWith("/") ? runName! : "/" + runName!;
            Console.WriteLine($"\nRunning: {input}");
            var r = commands.RunAsync(input, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"  isError      : {r.IsError}");
            Console.WriteLine($"  text         : {r.Text}");
            if (r.ClipboardText is not null) Console.WriteLine($"  clipboard    : {r.ClipboardText}");
            if (r.ImageUrl is not null) Console.WriteLine($"  image        : {r.ImageUrl}");
            if (r.Link is not null) Console.WriteLine($"  link         : {r.Link.Title} -> {r.Link.Url}");
            if (r.HoldSeconds is not null) Console.WriteLine($"  holdSeconds  : {r.HoldSeconds}");
        }

        bool anyBad = entries.Any(e => !e.Valid);
        Console.WriteLine(anyBad ? "\nFAIL: one or more manifests are invalid." : "\nOK: all manifests valid.");
        return anyBad || !reactionsOk ? 1 : 0;
    }

    /// <summary>Verify the <c>reaction:</c> parser: bare value, bracketed list, per-option hold seconds, and
    /// non-positive/blank durations falling back to default. Picking is uniform-random so we assert the parsed
    /// option set, not which one is drawn.</summary>
    private static bool CheckReactionParsing()
    {
        Console.WriteLine("reaction parsing:");
        int fails = 0;

        void Case(string value, params (string expr, double? secs)[] expected)
        {
            var (m, err) = CommandManifest.Parse($"---\nname: t\nkind: text\nreaction: {value}\n---\nbody");
            var got = m?.Reactions ?? new();
            bool ok = err is null && got.Count == expected.Length &&
                      got.Zip(expected).All(p => p.First.Expression == p.Second.expr && p.First.Seconds == p.Second.secs);
            if (!ok) fails++;
            string show(IEnumerable<(string, double?)> xs) =>
                "[" + string.Join(", ", xs.Select(x => x.Item2 is { } s ? $"{x.Item1}|{s:0.##}" : x.Item1)) + "]";
            Console.WriteLine($"  {(ok ? "OK " : "BAD")}  reaction: {value,-24} -> {show(got)}" +
                              (ok ? "" : $"   (expected {show(expected.Select(e => (e.expr, e.secs)))})"));
        }

        Case("smile|60", ("smile", 60));                                 // single, with hold seconds
        Case("smile", ("smile", null));                                  // single, default hold
        Case("[smile|30, blink|20]", ("smile", 30), ("blink", 20));      // list, both with seconds
        Case("[smile]", ("smile", null));                                // list of one
        Case("[smile, blink|60]", ("smile", null), ("blink", 60));       // list, mixed
        Case("[ smile|30 ,  blink ]", ("smile", 30), ("blink", null));   // whitespace tolerance
        Case("[smile|0, blink|-3]", ("smile", null), ("blink", null));   // non-positive seconds -> default
        Case("", Array.Empty<(string, double?)>());                      // empty value -> no reaction
        Case("[]", Array.Empty<(string, double?)>());                    // empty list -> no reaction

        Console.WriteLine(fails == 0 ? "  OK: reaction cases pass." : $"  FAIL: {fails} reaction case(s).");
        Console.WriteLine();
        return fails == 0;
    }
}
