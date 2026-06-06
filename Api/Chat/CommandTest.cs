using System;
using System.Linq;
using System.Threading;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

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
            scriptsEnabled: () => true);

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
        return anyBad ? 1 : 0;
    }
}
