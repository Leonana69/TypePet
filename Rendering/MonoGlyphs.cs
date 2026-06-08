using System.Collections.Generic;
using Avalonia.Media;

namespace TypePet.Rendering;

/// <summary>
/// Helpers for rendering Box Drawing / Block Elements glyphs (U+2500–U+259F) — e.g. the <c>/rank</c>
/// EXP bar's full block <c>█</c> and light shade <c>░</c>.
///
/// Windows' default font (<see cref="Typeface.Default"/>, Segoe UI) already tiles <c>█</c>/<c>░</c> at a
/// uniform height, so the bar renders cleanly with no override. macOS' default proportional font does not:
/// it draws the shade <c>░</c> shorter than the full block <c>█</c> and as a sparse dither with gaps, so
/// the bar looks ragged (the filled part sits higher than the track). There we switch ONLY these glyphs to
/// a monospace face (Menlo), which tiles the whole family at a uniform cell height, leaving surrounding
/// prose in the proportional font.
///
/// Hence <see cref="Mono"/> is <c>null</c> on Windows (no override) and a monospace family elsewhere. Note
/// the obvious "use Consolas on Windows too" does the *opposite* of help: in Consolas <c>░</c> sits visibly
/// higher than <c>█</c> — the regression this null-on-Windows split exists to avoid. Each font has its own
/// vertical offset between the two glyphs, so there is no single family that aligns them everywhere.
/// </summary>
public static class MonoGlyphs
{
    /// <summary>The monospace family to draw box/block glyphs in, or <c>null</c> when the platform's default
    /// font already renders <c>█</c>/<c>░</c> at matching heights and no override is wanted. Null on Windows;
    /// Menlo on macOS; DejaVu Sans Mono on Linux (each with in-family fallbacks).</summary>
    public static readonly FontFamily? Mono =
        System.OperatingSystem.IsWindows() ? null
        : System.OperatingSystem.IsMacOS() ? new("Menlo, Monaco, monospace")
        : new("DejaVu Sans Mono, Liberation Mono, monospace");

    /// <summary>True for Box Drawing (U+2500–U+257F) and Block Elements (U+2580–U+259F) glyphs — the
    /// characters we want drawn in <see cref="Mono"/>. Ordinary text never contains these.</summary>
    public static bool IsBoxGlyph(char c) => c is >= '─' and <= '▟';

    /// <summary>Yields the maximal runs of <see cref="IsBoxGlyph"/> characters in <paramref name="s"/> as
    /// (start index, length) pairs, so callers can restyle just those spans.</summary>
    public static IEnumerable<(int Start, int Length)> Spans(string s)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            if (!IsBoxGlyph(s[i])) { i++; continue; }
            int start = i;
            while (i < n && IsBoxGlyph(s[i])) i++;
            yield return (start, i - start);
        }
    }
}
