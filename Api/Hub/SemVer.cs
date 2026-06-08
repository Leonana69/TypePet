using System;

namespace MaplePet.Api.Hub;

/// <summary>
/// A tiny, lenient semantic-version comparison — the single home for "is version A newer than B" used by both
/// hub update-detection (installed vs available) and compatibility gating (app version vs a command's
/// <c>minAppVersion</c>). Compares numeric <c>major.minor.patch</c> only; a <c>-prerelease</c>/<c>+build</c>
/// suffix and any missing or unparseable component are ignored (treated as 0). Never throws.
/// </summary>
public static class SemVer
{
    /// <summary>&lt;0 if <paramref name="a"/> is older, 0 if equal (numerically), &gt;0 if newer.</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < 3; i++)
        {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>True if <paramref name="version"/> ≥ <paramref name="minimum"/>. A null/blank minimum means
    /// "no floor" ⇒ always true.</summary>
    public static bool AtLeast(string? version, string? minimum) =>
        string.IsNullOrWhiteSpace(minimum) || Compare(version, minimum) >= 0;

    private static int[] Parse(string? v)
    {
        var result = new int[3];
        if (string.IsNullOrWhiteSpace(v)) return result;
        int cut = v!.IndexOfAny(new[] { '-', '+' });   // strip prerelease/build metadata
        if (cut >= 0) v = v[..cut];
        var parts = v.Split('.');
        for (int i = 0; i < 3 && i < parts.Length; i++)
            int.TryParse(parts[i].Trim(), out result[i]);
        return result;
    }
}
