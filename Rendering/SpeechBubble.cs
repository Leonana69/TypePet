using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace MaplePet.Rendering;

/// <summary>
/// Draws a small speech bubble above (or, if there's no room, below) the pet — the control API's
/// <c>Say</c> channel. Text wraps to a max width; the bubble is clamped to stay on screen and a tail
/// points at the pet. Drawn as a light bubble: gray fill, black border, black text.
/// </summary>
public static class SpeechBubble
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)); // light gray
    private static readonly IBrush TextBrush = new SolidColorBrush(Colors.Black);
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x5C, 0xC4)); // link blue
    private static readonly IPen Border = new Pen(new SolidColorBrush(Colors.Black), 1);
    private static readonly IPen LinkUnderline = new Pen(LinkBrush, 1);

    private const double MaxTextWidth = 240;
    private const double PadX = 10, PadY = 7;
    private const double Radius = 9;
    private const double TailW = 12, TailH = 9;
    private const double GapToPet = 6;
    private const double FontSize = 13;
    private const double LinkGap = 5;  // vertical space between the message text and the link line
    private const double ImgGap = 6;   // vertical space between the image and the text
    // Images are scaled DOWN to fit this box and NEVER up, so the cap can be generous without bloating small
    // images: a /rank character canvas stays ~100px (its native crop), while a larger guide image (/esfera,
    // 417px) shows close to full size so its detail stays legible.
    private const double ImgMax = 420;

    /// <summary>Draw <paramref name="text"/> in a bubble whose tail points at (<paramref name="anchorX"/>,
    /// <paramref name="topY"/>) — the top-center of the drawn pet — clamped within <paramref name="screen"/>.
    /// An optional <paramref name="image"/> (e.g. the character canvas) is drawn centered on top; when
    /// <paramref name="linkLabel"/> is non-empty, a clickable, underlined link line is drawn below the text
    /// and its bounds (in the same logical coords as <paramref name="screen"/>) are returned so the caller
    /// can hit-test clicks on it; otherwise null.</summary>
    private const int MaxChars = 700; // a speech bubble shouldn't show a wall of text; long replies are clipped

    public static MaplePet.Engine.Rect? Draw(DrawingContext ctx, string text, string? linkLabel, IImage? image,
        double anchorX, double topY, MaplePet.Engine.Rect screen)
    {
        text = Sanitize(text);
        if (string.IsNullOrEmpty(text)) return null;

        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, FontSize, TextBrush) { MaxTextWidth = MaxTextWidth };

        // Optional link line (its own underlined, blue FormattedText below the message).
        string link = Sanitize(linkLabel ?? "");
        FormattedText? fl = string.IsNullOrEmpty(link) ? null : new FormattedText(link,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, FontSize, LinkBrush)
        { MaxTextWidth = MaxTextWidth };

        // Optional character image, scaled uniformly (never up) to fit an ImgMax box and centered on top.
        double imgW = 0, imgH = 0;
        if (image is not null && image.Size.Width > 0 && image.Size.Height > 0)
        {
            double s = Math.Min(1.0, Math.Min(ImgMax / image.Size.Width, ImgMax / image.Size.Height));
            imgW = image.Size.Width * s;
            imgH = image.Size.Height * s;
        }

        double contentW = Math.Max(Math.Max(ft.Width, fl?.Width ?? 0), imgW);
        double contentH = (imgH > 0 ? imgH + ImgGap : 0) + ft.Height + (fl is null ? 0 : LinkGap + fl.Height);
        double w = Math.Ceiling(contentW) + PadX * 2;
        double h = Math.Ceiling(contentH) + PadY * 2;

        // Prefer above the pet (tail pointing down). If it would clip the top edge, place it below.
        bool below = topY - GapToPet - TailH - h < screen.Top + 4;
        double bx = Math.Clamp(anchorX - w / 2, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - w - 4));
        double by = below ? topY + GapToPet + TailH : topY - GapToPet - TailH - h;

        // Build the bubble body + tail as ONE rounded outline, so filling/stroking it once leaves no
        // seam line where the tail joins the body (a separate bordered rect + triangle draws the body's
        // border straight across the join). The tail springs from the bubble edge nearest the pet.
        double tailX = Math.Clamp(anchorX, bx + Radius + TailW / 2, bx + w - Radius - TailW / 2);
        double left = bx, right = bx + w, top = by, bottom = by + h, r = Radius;
        var corner = new Size(r, r);

        var bubble = new StreamGeometry();
        using (var g = bubble.Open())
        {
            g.BeginFigure(new Point(left + r, top), isFilled: true);
            if (below)
            {
                // tail on the top edge, pointing up at the pet
                g.LineTo(new Point(tailX - TailW / 2, top));
                g.LineTo(new Point(tailX, top - TailH));
                g.LineTo(new Point(tailX + TailW / 2, top));
            }
            g.LineTo(new Point(right - r, top));
            g.ArcTo(new Point(right, top + r), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(right, bottom - r));
            g.ArcTo(new Point(right - r, bottom), corner, 0, false, SweepDirection.Clockwise);
            if (!below)
            {
                // tail on the bottom edge, pointing down at the pet
                g.LineTo(new Point(tailX + TailW / 2, bottom));
                g.LineTo(new Point(tailX, bottom + TailH));
                g.LineTo(new Point(tailX - TailW / 2, bottom));
            }
            g.LineTo(new Point(left + r, bottom));
            g.ArcTo(new Point(left, bottom - r), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(left, top + r));
            g.ArcTo(new Point(left + r, top), corner, 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Fill, Border, bubble);

        // Stack the content top→bottom: image (centered), then text, then the link line.
        double y = by + PadY;
        if (imgH > 0)
        {
            ctx.DrawImage(image!, new Rect(bx + (w - imgW) / 2, y, imgW, imgH));
            y += imgH + ImgGap;
        }
        ctx.DrawText(ft, new Point(bx + PadX, y));
        y += ft.Height;

        if (fl is null) return null;
        y += LinkGap;
        double linkX = bx + PadX;
        ctx.DrawText(fl, new Point(linkX, y));
        // Underline it (FormattedText has no decoration property here), so it reads as a clickable link.
        double underlineY = y + fl.Height - 1.5;
        ctx.DrawLine(LinkUnderline, new Point(linkX, underlineY), new Point(linkX + fl.Width, underlineY));
        // The clickable region (in the same logical coords as `screen`), handed back for hit-testing.
        return new MaplePet.Engine.Rect(linkX, y, fl.Width, fl.Height);
    }

    /// <summary>Make arbitrary text safe to render: drop control characters (stray ANSI/escape codes from
    /// scraped pages, etc.) except newline/tab, and cap the length so a long reply can't blow up the bubble.</summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            if (sb.Length >= MaxChars) { sb.Append('…'); break; }
        }
        return sb.ToString().Trim();
    }
}
