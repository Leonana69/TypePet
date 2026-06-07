using System.Collections.Generic;
using Avalonia.Media;

namespace MaplePet.Rendering;

/// <summary>
/// Helpers for rendering Box Drawing / Block Elements glyphs (U+2500–U+259F) — e.g. the <c>/rank</c>
/// EXP bar's full block <c>█</c> and light shade <c>░</c> — in a monospace font.
///
/// The app's default proportional font (<see cref="Typeface.Default"/>) renders these inconsistently on
/// macOS: the shade glyph <c>░</c> is drawn shorter than the full block <c>█</c> and as a sparse dither
/// with gaps between cells, so the bar looks ragged (the filled part sits higher than the track). A
/// monospace face (Menlo / Consolas) tiles the whole family at a uniform cell height with no gaps, so the
/// bar lines up the same way it already does on Windows. We switch ONLY these glyphs to monospace, leaving
/// surrounding prose in the proportional font.
/// </summary>
public static class MonoGlyphs
{
    /// <summary>A monospace family per platform (with in-family fallbacks). Menlo is verified to render
    /// <c>█</c>/<c>░</c> at matching heights on macOS; Consolas does the same on Windows.</summary>
    public static readonly FontFamily Mono = new(
        System.OperatingSystem.IsWindows() ? "Consolas, Cascadia Mono, monospace"
        : System.OperatingSystem.IsMacOS() ? "Menlo, Monaco, monospace"
        : "DejaVu Sans Mono, Liberation Mono, monospace");

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
