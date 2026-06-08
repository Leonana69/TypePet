using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TypePet.Engine;

/// <summary>One installed user command: its <see cref="Id"/> (the on-disk folder name, e.g.
/// <c>cmd_1a2b3c4d</c>), the parsed <see cref="Name"/> + <see cref="Kind"/> + <see cref="Help"/> for the
/// management UI, its <see cref="Directory"/>, and whether it parsed/validated (<see cref="Valid"/> /
/// <see cref="Error"/>). An invalid entry is listed (so the user can see why) but never run.</summary>
public sealed record CommandEntry(
    string Id, string Name, string Kind, string Help, string Directory, bool Valid, string? Error,
    string? Version = null, string? Author = null, HubProvenance? Hub = null);

/// <summary>A command extracted + validated into a temp staging area but NOT yet published into the watched
/// store root. The hub installer writes provenance + records settings (e.g. disabling a script) against the
/// reserved <see cref="Id"/> while the folder is still invisible, THEN calls
/// <see cref="CommandStore.CompleteInstall"/> to publish it atomically — so a freshly installed script is
/// already recorded/disabled no matter when the file watcher fires (no race). Use
/// <see cref="CommandStore.DiscardStaged"/> to abandon it instead.</summary>
public sealed record StagedInstall(
    string Id, string SourceDir, string StagingRoot, CommandManifest? Manifest, bool Valid, string? Error);

/// <summary>
/// The on-disk library of user commands — the command counterpart of <see cref="CharacterStore"/>. Each
/// command is a folder under <see cref="Root"/> (<c>Commands/cmd_xxxxxxxx</c>) holding a
/// <c>command.md</c> (frontmatter + body) plus any asset files it references (images). Dropping a folder
/// in (or editing one) is the "upload" channel; a <c>CommandWatcher</c> hot-reloads on change.
///
/// Pure file IO with no Avalonia dependency: enumerating, reading + parsing a manifest, and deleting.
/// Turning a manifest into a runnable command is <c>CommandInterpreter</c>'s job (Api/Chat). Bundled
/// starter commands are seeded out of the app bundle by the host on first run (see the app's seeding).
/// </summary>
public sealed class CommandStore
{
    public const string ManifestFile = "command.md";

    /// <summary>The hub-provenance sidecar written into an installed command's folder (see
    /// <see cref="HubProvenance"/>). Distinct from the hub repo's source-side <c>hub.meta.json</c>.</summary>
    public const string ProvenanceFile = "hub.provenance.json";

    private static readonly JsonSerializerOptions HubJson =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    /// <summary>The directory user commands live in. Created on construction if missing.</summary>
    public string Root { get; }

    public CommandStore(string? root = null)
    {
        Root = root ?? ResolveDefaultRoot();
        try { Directory.CreateDirectory(Root); } catch { /* best effort; List re-checks */ }
    }

    /// <summary>
    /// Resolve the commands folder. Run from source it walks up to the project root and uses
    /// <c>&lt;repo&gt;/Assets/Commands</c> (beside the other assets); a published build with no project
    /// file falls back to <c>%LOCALAPPDATA%\TypePet\Commands</c> (writable per-user). Mirrors
    /// <see cref="CharacterStore.ResolveDefaultRoot"/>.
    /// </summary>
    public static string ResolveDefaultRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TypePet.csproj")))
                    return Path.Combine(dir.FullName, "Assets", "Commands");
                dir = dir.Parent;
            }
        }
        catch { /* fall through to app-data */ }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypePet", "Commands");
    }

    /// <summary>Every command folder under <see cref="Root"/> (parsed for name/kind/validity), sorted by id.
    /// A folder without a <c>command.md</c> is skipped; one with a malformed manifest is listed as invalid.</summary>
    public IReadOnlyList<CommandEntry> List()
    {
        var result = new List<CommandEntry>();
        try
        {
            if (!Directory.Exists(Root)) return result;
            foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dir, ManifestFile))) continue;
                result.Add(EntryFor(Path.GetFileName(dir), dir));
            }
        }
        catch { /* a transient IO error just yields what we have */ }
        return result;
    }

    /// <summary>Parse the manifest for <paramref name="id"/>, sanitized, or null if missing/unreadable.</summary>
    public CommandManifest? ReadManifest(string id)
    {
        var dir = SafeDir(id);
        if (dir is null) return null;
        var file = Path.Combine(dir, ManifestFile);
        if (!File.Exists(file)) return null;
        try
        {
            var (m, _) = CommandManifest.Parse(File.ReadAllText(file));
            m?.Sanitize();
            return m;
        }
        catch { return null; }
    }

    /// <summary>The folder of <paramref name="id"/> if it exists and holds a manifest, else null.</summary>
    public string? DirectoryFor(string id)
    {
        var dir = SafeDir(id);
        return dir is not null && File.Exists(Path.Combine(dir, ManifestFile)) ? dir : null;
    }

    /// <summary>Map an id to its folder, rejecting anything that could escape <see cref="Root"/>.</summary>
    public string? SafeDir(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Contains('/') || id.Contains('\\') || id.Contains("..")) return null;
        return Path.Combine(Root, id);
    }

    /// <summary>Delete a command folder (best effort).</summary>
    public void Delete(string id)
    {
        var dir = SafeDir(id);
        if (dir is not null && Directory.Exists(dir))
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>A fresh, unused <c>cmd_xxxxxxxx</c> folder name.</summary>
    public string NewId()
    {
        string id;
        do { id = "cmd_" + Guid.NewGuid().ToString("N")[..8]; }
        while (Directory.Exists(Path.Combine(Root, id)));
        return id;
    }

    // -------------------------------------------------------------------- import / export (phase 2)
    /// <summary>Import a command from a zip (like <see cref="Export"/> produces): locate the shallowest
    /// <c>command.md</c> and land its folder in a fresh <c>cmd_xxxxxxxx</c>. Throws on a zip with no
    /// manifest. Wired into the UI in a later phase; the core path keeps it available for tests.</summary>
    public CommandEntry Import(string zipPath)
    {
        Directory.CreateDirectory(Root);
        string id = NewId();
        string dest = Path.Combine(Root, id);
        string temp = Path.Combine(Path.GetTempPath(), "TypePet_cmd_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(zipPath, temp);
            string srcRoot = FindManifestRoot(temp)
                ?? throw new InvalidDataException("The zip does not contain a command.md.");
            MoveDirectory(srcRoot, dest);
            return EntryFor(id, dest);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Write a command folder to <paramref name="destZip"/> wrapped in a single top-level folder
    /// named after the command (the layout <see cref="Import"/> expects).</summary>
    public void Export(string id, string destZip)
    {
        var dir = DirectoryFor(id);
        if (dir is null) return;
        string folderName = SanitizeFileName(ReadManifest(id)?.Name ?? id);
        string temp = Path.Combine(Path.GetTempPath(), "TypePet_cmd_export_" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(temp, folderName);
        try
        {
            CopyDirectory(dir, staging);
            if (File.Exists(destZip)) File.Delete(destZip);
            ZipFile.CreateFromDirectory(temp, destZip);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- hub install (race-proof, staged)
    /// <summary>Stage a hub command from a zip WITHOUT publishing it into the watched root yet: extract +
    /// validate into a temp dir and reserve a fresh id. The caller writes provenance into
    /// <see cref="StagedInstall.SourceDir"/> and records settings against the reserved id, THEN calls
    /// <see cref="CompleteInstall"/> — so the command is fully recorded before the file watcher can see it.
    /// Throws on a zip with no manifest (the temp area is cleaned up).</summary>
    public StagedInstall PrepareInstall(string zipPath)
    {
        string staging = Path.Combine(Path.GetTempPath(), "TypePet_cmd_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            string srcRoot = FindManifestRoot(staging)
                ?? throw new InvalidDataException("The zip does not contain a command.md.");
            var (m, _) = CommandManifest.Parse(File.ReadAllText(Path.Combine(srcRoot, ManifestFile)));
            m?.Sanitize();
            string? verr = m?.Validate();
            return new StagedInstall(NewId(), srcRoot, staging, m, m is not null && verr is null, verr);
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    /// <summary>Publish a previously <see cref="PrepareInstall"/>ed command into the store root atomically and
    /// return its entry. The staging area is cleaned up afterward.</summary>
    public CommandEntry CompleteInstall(StagedInstall staged)
    {
        Directory.CreateDirectory(Root);
        string dest = Path.Combine(Root, staged.Id);
        try { MoveDirectory(staged.SourceDir, dest); }
        finally { try { if (Directory.Exists(staged.StagingRoot)) Directory.Delete(staged.StagingRoot, true); } catch { } }
        return EntryFor(staged.Id, dest);
    }

    /// <summary>Abandon a staged install (e.g. the user cancelled), deleting its temp area.</summary>
    public void DiscardStaged(StagedInstall staged)
    {
        try { if (Directory.Exists(staged.StagingRoot)) Directory.Delete(staged.StagingRoot, true); } catch { }
    }

    // ---------------------------------------------------------------- hub provenance + content hashing
    /// <summary>Read the hub-provenance sidecar for <paramref name="id"/>, or null if absent/unreadable.</summary>
    public HubProvenance? ReadProvenance(string id)
    {
        var dir = SafeDir(id);
        return dir is null ? null : ReadProvenanceFromDir(dir);
    }

    private static HubProvenance? ReadProvenanceFromDir(string dir)
    {
        try
        {
            var f = Path.Combine(dir, ProvenanceFile);
            return File.Exists(f) ? JsonSerializer.Deserialize<HubProvenance>(File.ReadAllText(f), HubJson) : null;
        }
        catch { return null; }
    }

    /// <summary>Write the hub-provenance sidecar for <paramref name="id"/> (best effort).</summary>
    public void WriteProvenance(string id, HubProvenance p)
    {
        var dir = SafeDir(id);
        if (dir is not null) WriteProvenanceFile(dir, p);
    }

    /// <summary>Write the provenance sidecar into an arbitrary folder (used for a staged install dir, before
    /// it is published into the store).</summary>
    public static void WriteProvenanceFile(string dir, HubProvenance p)
    {
        try { File.WriteAllText(Path.Combine(dir, ProvenanceFile), JsonSerializer.Serialize(p, HubJson)); }
        catch { /* best effort */ }
    }

    /// <summary>A deterministic hash over a command folder's files — sorted relative paths + bytes, EXCLUDING
    /// the <see cref="ProvenanceFile"/> itself — so it reflects only the command's own content. Used to detect
    /// local edits before an update overwrites them. Returns "" on any IO error.</summary>
    public static string ComputeContentHash(string dir)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => (rel: Path.GetRelativePath(dir, f).Replace('\\', '/'), full: f))
                .Where(x => !x.rel.Equals(ProvenanceFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.rel, StringComparer.Ordinal)
                .ToList();
            using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (rel, full) in files)
            {
                ih.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
                ih.AppendData(File.ReadAllBytes(full));
            }
            return Convert.ToHexString(ih.GetHashAndReset()).ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>Build a <see cref="CommandEntry"/> for an on-disk command folder: parse + sanitize + validate
    /// its manifest and attach any hub provenance. Shared by <see cref="List"/>, <see cref="Import"/>, and
    /// <see cref="CompleteInstall"/> so they report identical metadata.</summary>
    private CommandEntry EntryFor(string id, string dir)
    {
        HubProvenance? hub = ReadProvenanceFromDir(dir);
        var file = Path.Combine(dir, ManifestFile);
        if (!File.Exists(file)) return new CommandEntry(id, id, "", "", dir, false, "no command.md", Hub: hub);
        try
        {
            var (m, err) = CommandManifest.Parse(File.ReadAllText(file));
            if (m is null) return new CommandEntry(id, id, "", "", dir, false, err ?? "invalid", Hub: hub);
            m.Sanitize();
            string? verr = m.Validate();
            return new CommandEntry(
                id, string.IsNullOrEmpty(m.Name) ? id : m.Name,
                m.Kind == CommandKind.Unknown ? (m.RawKind ?? "") : m.Kind.ToString().ToLowerInvariant(),
                m.Help ?? "", dir, verr is null, verr, m.Version, m.Author, hub);
        }
        catch (Exception ex)
        {
            return new CommandEntry(id, id, "", "", dir, false, ex.Message, Hub: hub);
        }
    }

    private static string? FindManifestRoot(string dir)
    {
        var manifest = Directory
            .EnumerateFiles(dir, ManifestFile, SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        return manifest is null ? null : Path.GetDirectoryName(manifest);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "command" : clean;
    }

    private static void MoveDirectory(string src, string dest)
    {
        try { Directory.Move(src, dest); return; }
        catch { /* cross-volume; fall back to copy */ }
        CopyDirectory(src, dest);
        try { Directory.Delete(src, true); } catch { }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }
}
