using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace TypePet.Engine;

/// <summary>One selectable character: a stable <see cref="Id"/> (the on-disk folder name, e.g.
/// <c>char_1a2b3c4d</c>), the user-facing <see cref="DisplayName"/>, the <see cref="Directory"/> its
/// footage lives in (null for every built-in), and whether it is one of the built-ins.</summary>
public sealed record CharacterEntry(string Id, string DisplayName, string? Directory, bool IsBuiltIn);

/// <summary>
/// Manages the on-disk library of pet characters. Each character is a folder under <see cref="Root"/>
/// (<c>Assets/Characters/char_xxxxxxxx</c>) holding a <c>manifest.json</c> and its item folders — the
/// same layout as the bundled footage — plus an optional <c>character.json</c> storing the user's
/// custom display name (the folder name never changes). The built-ins (the blob "default" + the
/// "pig", both embedded) are always present; the blob default is the fallback when no character is
/// selected or one is deleted.
///
/// Pure file IO with no Avalonia dependency: enumerating, importing a zip, exporting a zip, deleting,
/// and renaming. Decoding the footage into drawable sprites is the renderer's job
/// (<c>CharacterSprites.LoadFromDirectory</c>).
/// </summary>
public sealed class CharacterStore
{
    /// <summary>The reserved id of the always-present built-in blob character.</summary>
    public const string DefaultId = "default";

    /// <summary>The reserved id of the built-in pig character.</summary>
    public const string PigId = "pig";

    /// <summary>The built-in characters shipped inside the app: reserved id, display name, and the
    /// embedded (avares) footage folder each renders from. Always present, never on disk.</summary>
    private static readonly (string Id, string Name, string Footage)[] BuiltInTable =
    {
        (DefaultId, "Default", "Assets/DefaultCharacters/TypePet"),
        (PigId, "Pig", "Assets/DefaultCharacters/Pig"),
    };

    private const string ManifestFile = "manifest.json";
    private const string MetaFile = "character.json"; // stores the custom display name

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The directory user characters live in. Created on construction if missing.</summary>
    public string Root { get; }

    public CharacterStore(string? root = null)
    {
        Root = root ?? ResolveDefaultRoot();
        try { Directory.CreateDirectory(Root); } catch { /* best effort; List/Import re-create as needed */ }
    }

    /// <summary>
    /// Resolve the characters folder. When run from the source tree (the normal case) this walks up
    /// from the executable to the project root and uses <c>&lt;repo&gt;/Assets/Characters</c>, so
    /// characters sit beside the rest of the assets. When that can't be found (a published build with
    /// no project file), fall back to the per-user app-data folder so the location is still writable.
    /// </summary>
    public static string ResolveDefaultRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TypePet.csproj")))
                    return Path.Combine(dir.FullName, "Assets", "Characters");
                dir = dir.Parent;
            }
        }
        catch { /* fall through to app-data */ }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypePet", "Characters");
    }

    /// <summary>The built-in blob default (no folder; rendered from the embedded footage).</summary>
    public static CharacterEntry BuiltInDefault() => new(DefaultId, "Default", null, true);

    /// <summary>Every built-in character, in display order.</summary>
    public static IReadOnlyList<CharacterEntry> BuiltIns()
    {
        var result = new List<CharacterEntry>(BuiltInTable.Length);
        foreach (var (id, name, _) in BuiltInTable) result.Add(new CharacterEntry(id, name, null, true));
        return result;
    }

    /// <summary>The embedded footage folder for a built-in id (e.g. <c>Assets/DefaultCharacters/Pig</c>),
    /// or null when the id isn't a built-in. The renderer-side loader uses this to pick the
    /// avares folder to decode.</summary>
    public static string? EmbeddedFootage(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        // Case-insensitive: ids land in folder names on case-insensitive filesystems, so a
        // manually created "Pig"/"PIG" dir must still resolve to (and never shadow) a built-in.
        foreach (var (bid, _, footage) in BuiltInTable)
            if (string.Equals(id, bid, StringComparison.OrdinalIgnoreCase)) return footage;
        return null;
    }

    /// <summary>The built-ins first, then every valid character folder under <see cref="Root"/>, sorted.</summary>
    public IReadOnlyList<CharacterEntry> List()
    {
        var result = new List<CharacterEntry>(BuiltIns());
        try
        {
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(Path.Combine(dir, ManifestFile))
                        && EmbeddedFootage(Path.GetFileName(dir)) is null) // a dir named like a built-in id would shadow it
                        result.Add(EntryFor(dir));
        }
        catch { /* a transient IO error just yields the default-only list */ }
        return result;
    }

    /// <summary>The entry for <paramref name="id"/>, or null if it isn't a known character. The
    /// empty id resolves to the built-in default; the reserved ids to their built-ins.</summary>
    public CharacterEntry? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return BuiltInDefault();
        foreach (var (bid, name, _) in BuiltInTable)
            if (string.Equals(id, bid, StringComparison.OrdinalIgnoreCase))
                return new CharacterEntry(bid, name, null, true);
        var dir = SafeDir(id);
        if (dir is not null && File.Exists(Path.Combine(dir, ManifestFile))) return EntryFor(dir);
        return null;
    }

    private CharacterEntry EntryFor(string dir)
    {
        string id = Path.GetFileName(dir);
        return new CharacterEntry(id, ReadDisplayName(dir) ?? id, dir, false);
    }

    /// <summary>Map an id to its folder, rejecting anything that could escape <see cref="Root"/>.</summary>
    private string? SafeDir(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (EmbeddedFootage(id) is not null) return null; // built-in ids never map to a folder
        if (id.Contains('/') || id.Contains('\\') || id.Contains("..")) return null;
        return Path.Combine(Root, id);
    }

    // ------------------------------------------------------------------ display name (character.json)
    private sealed class Meta { public string? DisplayName { get; set; } }

    private string? ReadDisplayName(string dir)
    {
        try
        {
            var path = Path.Combine(dir, MetaFile);
            if (!File.Exists(path)) return null;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(path), JsonOpts);
            var name = meta?.DisplayName?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>Set a character's custom display name (persisted in <c>character.json</c>, folder name
    /// unchanged). Clearing it — empty or equal to the id — removes the override so it shows the id.</summary>
    public void Rename(string id, string? displayName)
    {
        var dir = SafeDir(id);
        if (dir is null || !Directory.Exists(dir)) return;

        var name = displayName?.Trim();
        var path = Path.Combine(dir, MetaFile);
        try
        {
            if (string.IsNullOrEmpty(name) || name == id)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new Meta { DisplayName = name }, JsonOpts));
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// True if a character OTHER than <paramref name="exceptId"/> already shows
    /// <paramref name="displayName"/> (case-insensitive, trimmed) — including the built-in default.
    /// Used to warn before a rename would create two identically-named characters (which would also
    /// collide on the export file name). An empty/whitespace name never counts as taken.
    /// </summary>
    public bool IsNameTaken(string? displayName, string exceptId)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var entry in List())
            if (!string.Equals(entry.Id, exceptId, StringComparison.Ordinal)
                && string.Equals(entry.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ------------------------------------------------------------------ import
    /// <summary>
    /// Import a character from a zip (like the one <see cref="Export"/> produces). The footage may sit
    /// at the zip root or under a single wrapping folder; we locate the <c>manifest.json</c> and treat
    /// its folder as the character. Lands in a fresh <c>char_xxxxxxxx</c> folder with the default name
    /// (any bundled <c>character.json</c> is dropped). Throws on a zip with no manifest.
    /// </summary>
    public CharacterEntry Import(string zipPath)
    {
        Directory.CreateDirectory(Root);
        string id = NewId();
        string dest = Path.Combine(Root, id);
        string temp = Path.Combine(Path.GetTempPath(), "TypePet_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(zipPath, temp);

            string srcRoot = FindManifestRoot(temp)
                ?? throw new InvalidDataException("The zip does not contain a manifest.json.");

            MoveDirectory(srcRoot, dest);

            // Imported characters always start with the default name (char_xxxxxxxx); strip any name
            // that rode along in the zip so the exported-then-imported round trip behaves predictably.
            var meta = Path.Combine(dest, MetaFile);
            try { if (File.Exists(meta)) File.Delete(meta); } catch { }

            return EntryFor(dest);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>The folder of the shallowest <c>manifest.json</c> under <paramref name="dir"/>, or null.</summary>
    private static string? FindManifestRoot(string dir)
    {
        var manifest = Directory
            .EnumerateFiles(dir, ManifestFile, SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        return manifest is null ? null : Path.GetDirectoryName(manifest);
    }

    // ------------------------------------------------------------------ delete
    /// <summary>Delete a character folder. Built-ins are never deletable.</summary>
    public void Delete(string id)
    {
        var dir = SafeDir(id);
        if (dir is not null && Directory.Exists(dir))
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ export
    /// <summary>
    /// Write a character to <paramref name="destZip"/> in the same shape we import: the footage wrapped
    /// in a single top-level folder named after the display name. The custom-name file is excluded so
    /// the zip is portable footage only (the file name carries the name). No-op for built-ins,
    /// which have no footage folder on disk.
    /// </summary>
    public void Export(string id, string destZip)
    {
        var entry = Get(id);
        if (entry?.Directory is null) return; // built-in default has nothing on disk to export

        string folderName = SanitizeFileName(entry.DisplayName);
        string temp = Path.Combine(Path.GetTempPath(), "TypePet_export_" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(temp, folderName);
        try
        {
            CopyDirectory(entry.Directory, staging, skipFileName: MetaFile);
            if (File.Exists(destZip)) File.Delete(destZip);
            // temp holds exactly the single <folderName> tree, so the zip entries are
            // "<folderName>/manifest.json", ... — the layout Import expects.
            ZipFile.CreateFromDirectory(temp, destZip);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ helpers
    private string NewId()
    {
        string id;
        do { id = "char_" + Guid.NewGuid().ToString("N").Substring(0, 8); }
        while (Directory.Exists(Path.Combine(Root, id)));
        return id;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "character" : clean;
    }

    /// <summary>Move a directory, falling back to copy+delete when source and dest are on different
    /// volumes (the temp extract dir and the repo can be on separate drives).</summary>
    private static void MoveDirectory(string src, string dest)
    {
        try { Directory.Move(src, dest); return; }
        catch { /* cross-volume or other; fall back to copy */ }

        CopyDirectory(src, dest);
        try { Directory.Delete(src, true); } catch { }
    }

    private static void CopyDirectory(string src, string dest, string? skipFileName = null)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            if (skipFileName is not null &&
                string.Equals(Path.GetFileName(file), skipFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)), skipFileName);
    }
}
