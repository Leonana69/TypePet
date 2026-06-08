using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaplePet.Engine;

namespace MaplePet.Api.Hub;

/// <summary>How an update replaces an installed command.</summary>
public enum UpdateMode
{
    /// <summary>Replace the installed command in place (discarding any local edits), preserving the user's
    /// enable/disable choice and re-revoking the network grant if the hosts changed.</summary>
    Overwrite,

    /// <summary>Keep the installed (possibly edited) command and add the new version as a separate command.</summary>
    Copy,
}

/// <summary>
/// The app's client for the command hub. Fetches + caches the registry (<c>index.json</c>) and installs /
/// updates commands by downloading the per-command zip, VERIFYING its sha256 against the index entry (the
/// integrity anchor), then handing it to the race-proof staged install on <see cref="CommandStore"/>. All
/// network goes through <see cref="SafeHttp"/> (https-only, SSRF-safe, size-capped). The read path is
/// anonymous — no key. Transient index failures serve the stale cache; nothing here throws to the UI.
/// </summary>
public sealed class HubClient
{
    /// <summary>The default registry URL. Read from GitHub RAW (not jsDelivr) so it's always fresh: jsDelivr
    /// caches branch (<c>@main</c>) URLs for ~12h, so newly published commands took hours to appear even after
    /// a successful build. The per-version command zips (each entry's <c>download.url</c>) stay on jsDelivr —
    /// they're immutable, so CDN caching is correct there — with a raw fallback baked into each entry.</summary>
    public const string DefaultIndexUrl =
        "https://raw.githubusercontent.com/Leonana69/TypePet-Commands/main/dist/index.json";

    private const long MaxZipBytes = 25L * 1024 * 1024;
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = SafeHttp.CreateClient($"MaplePet/{AppInfo.Version}");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly string _indexSource;
    private readonly bool _allowLocal;       // local zip/index sources are permitted only in test/offline mode
    private readonly string _cachePath;
    private readonly string _metaPath;

    /// <param name="dataRoot">Writable app-data dir for the index cache.</param>
    /// <param name="indexSource">Override the registry URL — or a local file path for tests/offline.</param>
    public HubClient(string dataRoot, string? indexSource = null)
    {
        _indexSource = string.IsNullOrWhiteSpace(indexSource) ? DefaultIndexUrl : indexSource!;
        _allowLocal = !IsHttp(_indexSource);
        _cachePath = Path.Combine(dataRoot, "hub-index.json");
        _metaPath = Path.Combine(dataRoot, "hub-index.meta.json");
    }

    // ------------------------------------------------------------------ index fetch / cache
    private sealed class CacheMeta { public string? Etag { get; set; } public DateTime FetchedUtc { get; set; } }

    /// <summary>Return the registry, using a fresh local cache when possible. On a transient failure the stale
    /// cache is served; <c>error</c> is non-null only when nothing could be loaded at all.</summary>
    public async Task<(HubIndex index, string? error)> GetIndexAsync(bool force, CancellationToken ct)
    {
        if (!IsHttp(_indexSource))   // local file source (tests / offline)
        {
            try { return (Parse(await File.ReadAllTextAsync(_indexSource, ct).ConfigureAwait(false)), null); }
            catch (Exception ex) { return (new HubIndex(), ex.Message); }
        }

        var meta = ReadMeta();
        bool fresh = meta is not null && (DateTime.UtcNow - meta.FetchedUtc) < RefreshTtl;
        if (!force && fresh)
        {
            var cached = TryReadCache();
            if (cached is not null) return (cached, null);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _indexSource);
            if (meta?.Etag is { Length: > 0 } etag) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                WriteMeta(new CacheMeta { Etag = meta?.Etag, FetchedUtc = DateTime.UtcNow });
                var cached = TryReadCache();
                if (cached is not null) return (cached, null);
            }
            else if (resp.IsSuccessStatusCode)
            {
                byte[] bytes = await SafeHttp.ReadCappedAsync(resp, MaxZipBytes, cts.Token).ConfigureAwait(false);
                string text = Encoding.UTF8.GetString(bytes);
                var index = Parse(text);                       // throws on malformed json -> caught below
                WriteAtomic(_cachePath, text);
                WriteMeta(new CacheMeta { Etag = resp.Headers.ETag?.ToString(), FetchedUtc = DateTime.UtcNow });
                return (index, null);
            }
        }
        catch (Exception ex)
        {
            var cached = TryReadCache();
            return cached is not null ? (cached, null) : (new HubIndex(), ex.Message);
        }

        var fallback = TryReadCache();
        return fallback is not null ? (fallback, null) : (new HubIndex(), "could not load the hub index.");
    }

    private static HubIndex Parse(string text) =>
        JsonSerializer.Deserialize<HubIndex>(text, Json) ?? new HubIndex();

    private HubIndex? TryReadCache()
    {
        try { return File.Exists(_cachePath) ? Parse(File.ReadAllText(_cachePath)) : null; }
        catch { return null; }
    }

    private CacheMeta? ReadMeta()
    {
        try { return File.Exists(_metaPath) ? JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(_metaPath), Json) : null; }
        catch { return null; }
    }

    private void WriteMeta(CacheMeta m)
    {
        try { File.WriteAllText(_metaPath, JsonSerializer.Serialize(m, Json)); } catch { /* best effort */ }
    }

    private static void WriteAtomic(string path, string text)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ install / update
    /// <summary>Download + verify + install a hub command. A freshly installed <c>kind:script</c> command is
    /// recorded as DISABLED (in <paramref name="settings"/>) BEFORE it is published into the watched store, so
    /// it can never momentarily run. Does NOT touch the UI or rebuild the registry — this runs on a background
    /// thread (awaits use ConfigureAwait(false)); the caller rebuilds on the UI thread after awaiting, and the
    /// file watcher rebuilds independently when the folder lands.</summary>
    public async Task<InstallResult> InstallAsync(HubEntry entry, CommandStore store, Settings settings,
        CancellationToken ct)
    {
        StagedInstall? staged = null;
        string? zip = null;
        try
        {
            zip = await DownloadVerifiedZipAsync(entry, ct).ConfigureAwait(false);
            staged = store.PrepareInstall(zip);
            if (!staged.Valid)
            {
                store.DiscardStaged(staged);
                return new InstallResult(false, $"the downloaded command is invalid: {staged.Error}");
            }
            WriteProvenance(entry, staged);
            if (IsScript(staged) && !settings.DisabledCommandIds.Contains(staged.Id))
                settings.DisabledCommandIds.Add(staged.Id);   // start disabled until reviewed (before publish)
            settings.Save();
            var newEntry = store.CompleteInstall(staged);
            return new InstallResult(true, $"Installed /{newEntry.Name}.", newEntry);
        }
        catch (Exception ex)
        {
            if (staged is not null) store.DiscardStaged(staged);
            return new InstallResult(false, ex.Message);
        }
        finally { TryDelete(zip); }
    }

    /// <summary>Update an installed hub command. <see cref="UpdateMode.Copy"/> installs the new version as a
    /// separate command (keeping the existing one). <see cref="UpdateMode.Overwrite"/> replaces it in place:
    /// the new version is published BEFORE the old folder is deleted (so a failure leaves the working command
    /// intact), the user's enable/disable choice is migrated to the new id, and the network grant is carried
    /// over only if the declared hosts are unchanged.</summary>
    public async Task<InstallResult> UpdateAsync(HubEntry entry, string installedId, CommandStore store,
        Settings settings, UpdateMode mode, CancellationToken ct)
    {
        if (mode == UpdateMode.Copy)
            return await InstallAsync(entry, store, settings, ct).ConfigureAwait(false);

        bool wasDisabled = settings.DisabledCommandIds.Contains(installedId);
        var oldManifest = store.ReadManifest(installedId);
        string oldHostsSig = oldManifest?.HostsSignature() ?? "";
        bool wasNetApproved = settings.IsNetworkApproved(installedId, oldHostsSig);

        StagedInstall? staged = null;
        string? zip = null;
        try
        {
            zip = await DownloadVerifiedZipAsync(entry, ct).ConfigureAwait(false);
            staged = store.PrepareInstall(zip);
            if (!staged.Valid)
            {
                store.DiscardStaged(staged);
                return new InstallResult(false, $"the downloaded command is invalid: {staged.Error}");
            }
            WriteProvenance(entry, staged);

            string newId = staged.Id;
            string newHostsSig = staged.Manifest?.HostsSignature() ?? "";

            // Migrate the enable/disable choice to the new id.
            settings.DisabledCommandIds.RemoveAll(x => string.Equals(x, installedId, StringComparison.OrdinalIgnoreCase));
            if (wasDisabled) settings.DisabledCommandIds.Add(newId);

            // Carry the network grant ONLY if the hosts are unchanged; a changed allowlist forces re-approval.
            settings.NetworkApprovedCommands.RemoveAll(g =>
                g is not null && string.Equals(g.Id, installedId, StringComparison.OrdinalIgnoreCase));
            if (wasNetApproved && newHostsSig.Length > 0 && newHostsSig == oldHostsSig)
                settings.NetworkApprovedCommands.Add(new NetworkGrant { Id = newId, Hosts = newHostsSig });

            settings.Save();
            var newEntry = store.CompleteInstall(staged);   // publish new first
            store.Delete(installedId);                       // then remove old
            return new InstallResult(true, $"Updated /{newEntry.Name} to {entry.Version}.", newEntry);
        }
        catch (Exception ex)
        {
            if (staged is not null) store.DiscardStaged(staged);
            return new InstallResult(false, ex.Message);
        }
        finally { TryDelete(zip); }
    }

    /// <summary>True if the installed command's on-disk content no longer matches the hash recorded at install
    /// time — i.e. the user edited it locally, so an <see cref="UpdateMode.Overwrite"/> would discard those
    /// edits and the UI should prompt first.</summary>
    public static bool IsInstalledDirty(CommandStore store, string installedId)
    {
        var prov = store.ReadProvenance(installedId);
        var dir = store.DirectoryFor(installedId);
        if (prov is null || dir is null || string.IsNullOrEmpty(prov.ContentHash)) return false;
        return !string.Equals(CommandStore.ComputeContentHash(dir), prov.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteProvenance(HubEntry entry, StagedInstall staged)
    {
        var prov = new HubProvenance
        {
            HubId = entry.Id,
            Version = entry.Version,
            Sha256 = entry.Download.Sha256,
            ContentHash = CommandStore.ComputeContentHash(staged.SourceDir),
            InstalledUtc = DateTime.UtcNow.ToString("o"),
            Official = entry.Official,
        };
        CommandStore.WriteProvenanceFile(staged.SourceDir, prov);
    }

    private static bool IsScript(StagedInstall staged) => staged.Manifest?.Kind == CommandKind.Script;

    // ------------------------------------------------------------------ download + verify
    /// <summary>Download the zip and return a temp path only if its sha256 matches the index. Tries the
    /// primary URL then the fallback, moving on to the next source on EITHER a transport error OR a checksum
    /// mismatch — so a CDN serving a stale/wrong copy can't block an install when another source is good.</summary>
    private async Task<string> DownloadVerifiedZipAsync(HubEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.Download.Sha256))
            throw new InvalidDataException("the hub entry has no checksum; refusing to install.");
        string want = entry.Download.Sha256.Trim();

        string tmp = Path.Combine(Path.GetTempPath(), "MaplePet_hub_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            string? lastError = null;
            foreach (var url in new[] { entry.Download.Url, entry.Download.FallbackUrl })
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                byte[] bytes;
                try { bytes = await GetBytesAsync(url!, ct).ConfigureAwait(false); }
                catch (Exception ex) { lastError = ex.Message; continue; }   // transport error -> try next source

                string actual;
                using (var sha = SHA256.Create())
                    actual = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, want, StringComparison.OrdinalIgnoreCase))
                {
                    lastError = "checksum mismatch (a CDN may be serving a stale copy)";
                    continue;   // stale/wrong bytes -> try the next source rather than failing
                }
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                return tmp;
            }
            throw new InvalidDataException(lastError ?? "the download could not be verified.");
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        if (_allowLocal && !IsHttp(url))   // local zip (tests/offline) — only when the index itself is local
        {
            string p = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
            return await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false);
        }
        if (!SafeHttp.IsAllowedHttpsUrl(url))
            throw new HttpRequestException("blocked download URL (must be https to a public host).");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await SafeHttp.ReadCappedAsync(resp, MaxZipBytes, cts.Token).ConfigureAwait(false);
    }

    private static bool IsHttp(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
