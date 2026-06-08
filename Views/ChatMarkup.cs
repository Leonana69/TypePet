using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TypePet.Rendering;

namespace TypePet.Views;

/// <summary>
/// Lightweight inline-markdown rendering for chat replies. The model's answer is plain text that may
/// carry a little formatting (<c>**bold**</c>, <c>*italic*</c>) and links — both real URLs and bare
/// domains like <c>discord.gg/abc</c>. <see cref="BuildBlock"/> turns that into an Avalonia
/// <see cref="SelectableTextBlock"/> with styled runs and clickable link controls (so the body text stays
/// selectable while links are tappable). <see cref="ToSpoken"/> strips the emphasis markers for the pet's
/// speech bubble, which renders raw text and would otherwise show literal asterisks. Block-level markdown
/// (headings, lists, code fences, tables) is intentionally NOT handled — the system prompt steers the model
/// away from it since replies are short and spoken aloud.
/// </summary>
public static class ChatMarkup
{
    // Brighter teal matching the App.axaml "Button.link" style, so inline links read as links.
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x45, 0xD6, 0xD0));

    // One pass, left to right: bold-italic, bold, italic, then a link (scheme/www or a bare domain with a
    // known TLD + optional path). Emphasis groups are recursed into so a link inside **…** is still clickable.
    private static readonly Regex Token = new(
        @"\*\*\*(?<bi>.+?)\*\*\*" +
        @"|\*\*(?<b>.+?)\*\*" +
        @"|\*(?<i>[^*\r\n]+?)\*" +
        @"|(?<link>(?:https?://|www\.)[^\s<>()]+" +
        @"|[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9-]+)*\.(?:com|net|org|io|gg|tv|me|co|dev|app|wiki|gl|ly|info|xyz)(?:/[^\s<>()]*)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Emphasis markers paired around text; used to strip them from the spoken (raw-text) bubble.
    private static readonly Regex Emphasis = new(@"\*{1,3}(.+?)\*{1,3}", RegexOptions.Compiled);

    private const string TrailingPunctuation = ".,!?;:)]}\"'";

    /// <summary>A wrapping, selectable text block whose content is parsed for bold/italic and clickable
    /// links. <paramref name="baseForeground"/> is the default run colour (links override it); links open
    /// via <paramref name="openUrl"/>.</summary>
    public static SelectableTextBlock BuildBlock(string text, IBrush baseForeground, Action<string> openUrl)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = baseForeground };
        var inlines = new List<Inline>();
        Build(text ?? "", bold: false, italic: false, inlines, openUrl);
        if (inlines.Count == 0) inlines.Add(new Run(text ?? ""));
        foreach (var i in inlines) tb.Inlines!.Add(i);
        return tb;
    }

    /// <summary>Strip paired emphasis markers (<c>**</c>/<c>*</c>) so the spoken text shows no literal
    /// asterisks. Link text is left as-is (it's what gets read aloud). Lone, unpaired markers are kept.</summary>
    public static string ToSpoken(string text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : Emphasis.Replace(text, "$1");

    private static void Build(string text, bool bold, bool italic, IList<Inline> outp, Action<string> openUrl)
    {
        int pos = 0;
        foreach (Match m in Token.Matches(text))
        {
            if (m.Index > pos) AddRun(text.Substring(pos, m.Index - pos), bold, italic, outp);

            if (m.Groups["bi"].Success) Build(m.Groups["bi"].Value, true, true, outp, openUrl);
            else if (m.Groups["b"].Success) Build(m.Groups["b"].Value, true, italic, outp, openUrl);
            else if (m.Groups["i"].Success) Build(m.Groups["i"].Value, bold, true, outp, openUrl);
            else if (m.Groups["link"].Success)
            {
                string raw = m.Groups["link"].Value;
                string url = raw.TrimEnd(TrailingPunctuation.ToCharArray());
                AddLink(url, bold, italic, outp, openUrl);
                if (url.Length < raw.Length) AddRun(raw.Substring(url.Length), bold, italic, outp); // trailing "." etc.
            }

            pos = m.Index + m.Length;
        }
        if (pos < text.Length) AddRun(text.Substring(pos), bold, italic, outp);
    }

    private static void AddRun(string s, bool bold, bool italic, IList<Inline> outp)
    {
        if (s.Length == 0) return;
        // Emit block/box-drawing glyphs (the /rank EXP bar's █/░) as their own monospace runs so they tile at
        // a uniform height where the default proportional font mis-renders them (░ short and gappy on macOS).
        // On Windows MonoGlyphs.Mono is null — the default font already aligns █/░ — so emit one plain run.
        if (MonoGlyphs.Mono is not { } mono)
        {
            outp.Add(MakeRun(s, bold, italic, null));
            return;
        }
        int pos = 0;
        foreach (var (start, len) in MonoGlyphs.Spans(s))
        {
            if (start > pos) outp.Add(MakeRun(s.Substring(pos, start - pos), bold, italic, null));
            outp.Add(MakeRun(s.Substring(start, len), bold, italic, mono));
            pos = start + len;
        }
        if (pos < s.Length) outp.Add(MakeRun(s.Substring(pos), bold, italic, null));
    }

    private static Run MakeRun(string s, bool bold, bool italic, FontFamily? family)
    {
        var run = new Run(s)
        {
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        };
        if (family is not null) run.FontFamily = family;
        return run;
    }

    private static void AddLink(string url, bool bold, bool italic, IList<Inline> outp, Action<string> openUrl)
    {
        if (url.Length == 0) return;
        string href = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;

        var link = new TextBlock
        {
            Text = url,
            Foreground = LinkBrush,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(link, href);
        link.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) { openUrl(href); e.Handled = true; }
        };
        outp.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
    }
}
