namespace MaplePet.Engine;

/// <summary>
/// Records that an installed command came from the command hub, and at what version — written as a
/// <c>hub.provenance.json</c> sidecar INSIDE the command's <c>cmd_xxxxxxxx</c> folder (a name deliberately
/// distinct from the hub repo's source-side <c>hub.meta.json</c>, so the two never collide). Kept in the
/// folder rather than in <c>settings.json</c> so it travels with the command (survives export/import) and a
/// settings reset. <see cref="ContentHash"/> is a deterministic hash of the installed files captured at
/// install time, used to detect local edits before an update overwrites them
/// (<see cref="CommandStore.ComputeContentHash"/>).
/// </summary>
public sealed class HubProvenance
{
    /// <summary>The hub entry's stable id/slug this command was installed from (e.g. <c>rank</c>).</summary>
    public string HubId { get; set; } = "";

    /// <summary>The semantic version that was installed.</summary>
    public string Version { get; set; } = "";

    /// <summary>The sha256 of the downloaded zip (the index's integrity anchor) at install time.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Deterministic hash of the installed files (excluding this sidecar) at install time. If it no
    /// longer matches the folder's current content, the user edited the command locally (dirty).</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>When it was installed (ISO-8601 UTC), for display.</summary>
    public string InstalledUtc { get; set; } = "";

    /// <summary>Whether the source hub entry was flagged official/maintainer-published.</summary>
    public bool Official { get; set; }
}
