using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using MaplePet.Engine;

namespace MaplePet.Api.Hub;

/// <summary>
/// Dev-only headless check for the command hub client (no UI, no network). It builds local command zips in a
/// temp area, serves them through a file-based index, and exercises the full install/update path:
/// sha256 verification, the race-proof staged install (a freshly installed script is recorded DISABLED before
/// the folder is published), provenance round-trip + content hashing, dirty-local detection, and an Overwrite
/// update that migrates settings. Also checks <see cref="SemVer"/>. Wired to <c>--hub-test</c>.
/// </summary>
public static class HubTest
{
    public static int Run(string? arg)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        // If given a path to a real generated index.json, verify the actual artifacts install (sha256 +
        // extract + validate) instead of running the synthetic suite. Useful for checking the hub repo's
        // `dist/` output: `--hub-test <repo>/dist/index.json`.
        if (!string.IsNullOrWhiteSpace(arg) && File.Exists(arg!))
            return VerifyRealIndex(arg!);

        string sandbox = Path.Combine(Path.GetTempPath(), "MaplePet_hubtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        int fails = 0;
        try
        {
            fails += SemVerCases();
            fails += InstallAndUpdateCases(sandbox);
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { /* best effort */ }
        }

        Console.WriteLine(fails == 0 ? "\nOK: all hub-test cases pass." : $"\nFAIL: {fails} hub-test case(s).");
        return fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ verify a real generated index
    private static int VerifyRealIndex(string indexPath)
    {
        string indexDir = Path.GetDirectoryName(Path.GetFullPath(indexPath))!;
        string sandbox = Path.Combine(Path.GetTempPath(), "MaplePet_hubverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        int fails = 0;
        try
        {
            var store = new CommandStore(Path.Combine(sandbox, "Commands"));
            var settings = new Settings { SourcePath = Path.Combine(sandbox, "settings.json") };
            var hub = new HubClient(sandbox, indexPath);   // local index => local zip downloads allowed
            var (index, err) = hub.GetIndexAsync(force: true, CancellationToken.None).GetAwaiter().GetResult();

            Console.WriteLine($"Index: {indexPath}");
            Console.WriteLine($"  parsed: {index.Commands.Count} command(s), err={err ?? "none"}\n");
            if (err is not null) fails++;

            foreach (var e in index.Commands)
            {
                // The generated index points download.url at a CDN; map it to the local artifact so we can
                // install offline. sha256 in the index is still verified by the installer against these bytes.
                string local = Path.Combine(indexDir, "commands", e.Id, $"{e.Id}-{e.Version}.zip");
                if (!File.Exists(local)) { Console.WriteLine($"  BAD  {e.Id}: missing artifact {local}"); fails++; continue; }
                e.Download.Url = local;
                var r = hub.InstallAsync(e, store, settings, CancellationToken.None).GetAwaiter().GetResult();
                bool ok = r.Ok && r.Entry is { Valid: true };
                if (!ok) fails++;
                Console.WriteLine($"  {(ok ? "OK " : "BAD")}  {e.Id,-12} v{e.Version,-7} [{e.Kind}]  {(ok ? "installed + valid" : r.Message)}");
            }
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { /* best effort */ }
        }
        Console.WriteLine(fails == 0 ? "\nOK: every command in the index installs + validates." : $"\nFAIL: {fails} issue(s).");
        return fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ semver
    private static int SemVerCases()
    {
        Console.WriteLine("semver:");
        int fails = 0;
        void Case(string a, string b, int sign)
        {
            int got = Math.Sign(SemVer.Compare(a, b));
            bool ok = got == sign;
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "OK " : "BAD")}  {a,-10} vs {b,-10} -> {got,2} (want {sign,2})");
        }
        Case("1.2.0", "1.10.0", -1);   // numeric, not lexical
        Case("2.0.0", "1.9.9", +1);
        Case("1.0.0", "1.0.0", 0);
        Case("1.0", "1.0.0", 0);       // missing component == 0
        Case("1.2.3-rc1", "1.2.3", 0); // prerelease/build stripped
        Case("", "1.0.0", -1);         // blank == 0.0.0
        bool atLeast = SemVer.AtLeast("1.0.0", null) && SemVer.AtLeast("1.1.0", "1.0.0") && !SemVer.AtLeast("1.0.0", "1.1.0");
        if (!atLeast) fails++;
        Console.WriteLine($"  {(atLeast ? "OK " : "BAD")}  AtLeast (null floor / newer / older)");
        Console.WriteLine(fails == 0 ? "  OK: semver cases pass.\n" : $"  FAIL: {fails} semver case(s).\n");
        return fails;
    }

    // ------------------------------------------------------------------ install / update
    private static int InstallAndUpdateCases(string sandbox)
    {
        Console.WriteLine("install / update:");
        int fails = 0;
        bool Check(string label, bool ok)
        {
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "OK " : "BAD")}  {label}");
            return ok;
        }

        string storeRoot = Path.Combine(sandbox, "Commands");
        Directory.CreateDirectory(storeRoot);
        var store = new CommandStore(storeRoot);
        var settings = new Settings { SourcePath = Path.Combine(sandbox, "settings.json") };
        var hub = new HubClient(sandbox, Path.Combine(sandbox, "index.json")); // local index => local zips allowed
        var ct = CancellationToken.None;

        // --- index parse (fixture) ---
        string scriptV1Zip = MakeZip(sandbox, "rolltest", ScriptManifest("1.0.0", "say(\"v1\");"));
        WriteFixtureIndex(Path.Combine(sandbox, "index.json"),
            Entry("rolltest", "script", "1.0.0", scriptV1Zip),
            Entry("hello", "text", "1.0.0", MakeZip(sandbox, "hello", TextManifest())));
        var (index, idxErr) = hub.GetIndexAsync(force: true, ct).GetAwaiter().GetResult();
        Check($"index parsed ({index.Commands.Count} entries, err={idxErr ?? "none"})", idxErr is null && index.Commands.Count == 2);

        // --- install a SCRIPT command: must succeed and land DISABLED ---
        var scriptEntry = Entry("rolltest", "script", "1.0.0", scriptV1Zip);
        var r1 = hub.InstallAsync(scriptEntry, store, settings, ct).GetAwaiter().GetResult();
        string scriptId = r1.Entry?.Id ?? "";
        Check($"script install ok ({r1.Message})", r1.Ok);
        Check("installed script is DISABLED", scriptId.Length > 0 && settings.DisabledCommandIds.Contains(scriptId));
        var prov = store.ReadProvenance(scriptId);
        Check("provenance round-trips (hubId + version)", prov is { HubId: "rolltest", Version: "1.0.0" } && prov.ContentHash.Length > 0);

        // content hash is stable + matches the recorded value (no edits yet)
        string dir = store.DirectoryFor(scriptId) ?? "";
        Check("content hash matches recorded (clean)", dir.Length > 0 && CommandStore.ComputeContentHash(dir) == prov!.ContentHash);
        Check("not dirty right after install", !HubClient.IsInstalledDirty(store, scriptId));

        // --- install a DECLARATIVE command: must land ENABLED ---
        var textEntry = Entry("hello", "text", "1.0.0", MakeZip(sandbox, "hello", TextManifest()));
        var r2 = hub.InstallAsync(textEntry, store, settings, ct).GetAwaiter().GetResult();
        string textId = r2.Entry?.Id ?? "";
        Check($"text install ok ({r2.Message})", r2.Ok);
        Check("installed text is ENABLED (not disabled)", textId.Length > 0 && !settings.DisabledCommandIds.Contains(textId));

        // --- checksum mismatch is rejected ---
        var badEntry = Entry("rolltest", "script", "1.0.0", scriptV1Zip);
        badEntry.Download.Sha256 = new string('0', 64);
        var rBad = hub.InstallAsync(badEntry, store, settings, ct).GetAwaiter().GetResult();
        Check("checksum mismatch rejected", !rBad.Ok && rBad.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase));

        // --- dirty-local detection: edit the installed script, then it reports dirty ---
        File.AppendAllText(Path.Combine(dir, CommandStore.ManifestFile), "\n// local edit\n");
        Check("dirty after local edit", HubClient.IsInstalledDirty(store, scriptId));

        // --- Overwrite update to v2: replaces in place, bumps version, discards the edit ---
        string scriptV2Zip = MakeZip(sandbox, "rolltest", ScriptManifest("2.0.0", "say(\"v2 changed\");"));
        var v2Entry = Entry("rolltest", "script", "2.0.0", scriptV2Zip);
        var r3 = hub.UpdateAsync(v2Entry, scriptId, store, settings, UpdateMode.Overwrite, ct).GetAwaiter().GetResult();
        string newId = r3.Entry?.Id ?? "";
        Check($"overwrite update ok ({r3.Message})", r3.Ok);
        Check("old folder removed", store.DirectoryFor(scriptId) is null);
        var prov2 = store.ReadProvenance(newId);
        Check("provenance updated to v2", prov2 is { HubId: "rolltest", Version: "2.0.0" });
        string newDir = store.DirectoryFor(newId) ?? "";
        Check("v2 content clean (edit discarded)", newDir.Length > 0 && CommandStore.ComputeContentHash(newDir) == prov2!.ContentHash && !HubClient.IsInstalledDirty(store, newId));
        Check("updated script stays disabled (enable choice migrated)", settings.DisabledCommandIds.Contains(newId) && !settings.DisabledCommandIds.Contains(scriptId));

        // exactly two commands installed now (rolltest v2 + hello)
        var listed = store.List();
        Check($"store has 2 commands ({listed.Count})", listed.Count == 2 && listed.Count(e => e.Hub?.HubId == "rolltest") == 1);

        Console.WriteLine(fails == 0 ? "  OK: install/update cases pass.\n" : $"  FAIL: {fails} install/update case(s).\n");
        return fails;
    }

    // ------------------------------------------------------------------ fixtures
    private static HubEntry Entry(string id, string kind, string version, string zipPath) => new()
    {
        Id = id, Name = id, Title = id, Version = version, Author = "tester", Kind = kind,
        Official = false,
        Download = new HubDownload
        {
            Url = zipPath,
            Bytes = new FileInfo(zipPath).Length,
            Sha256 = Sha256Hex(zipPath),
        },
    };

    private static string ScriptManifest(string version, string body) =>
        $"---\nname: rolltest\nkind: script\nversion: {version}\nauthor: tester\nhelp: a test script\n---\n{body}\n";

    private static string TextManifest() =>
        "---\nname: hello\nkind: text\nversion: 1.0.0\nauthor: tester\nhelp: a greeting\n---\nHello there!\n";

    /// <summary>Build a single-top-level-folder zip (the shape Import/PrepareInstall expect) and return its path.</summary>
    private static string MakeZip(string sandbox, string slug, string manifestText)
    {
        string stage = Path.Combine(sandbox, "mk_" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(stage, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, CommandStore.ManifestFile), manifestText);
        string zip = Path.Combine(sandbox, "zip_" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(stage, zip);
        try { Directory.Delete(stage, true); } catch { /* best effort */ }
        return zip;
    }

    private static void WriteFixtureIndex(string path, params HubEntry[] entries)
    {
        var index = new HubIndex { SchemaVersion = 1, Commands = entries.ToList() };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(index,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sha256Hex(string file)
    {
        using var s = File.OpenRead(file);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
    }
}
