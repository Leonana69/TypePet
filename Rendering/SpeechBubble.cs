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
    private static readonly IPen Border = new Pen(new SolidColorBrush(Colors.Black), 1);

    private const double MaxTextWidth = 240;
    private const double PadX = 10, PadY = 7;
    private const double Radius = 9;
    private const double TailW = 12, TailH = 9;
    private const double GapToPet = 6;
    private const double FontSize = 13;

    /// <summary>Draw <paramref name="text"/> in a bubble whose tail points at (<paramref name="anchorX"/>,
    /// <paramref name="topY"/>) — the top-center of the drawn pet — clamped within <paramref name="screen"/>.</summary>
    private const int MaxChars = 700; // a speech bubble shouldn't show a wall of text; long replies are clipped

    public static void Draw(DrawingContext ctx, string text, double anchorX, double topY, MaplePet.Engine.Rect screen)
    {
        text = Sanitize(text);
        if (string.IsNullOrEmpty(text)) return;

        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, FontSize, TextBrush) { MaxTextWidth = MaxTextWidth };

        double w = Math.Ceiling(ft.Width) + PadX * 2;
        double h = Math.Ceiling(ft.Height) + PadY * 2;

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

        ctx.DrawText(ft, new Point(bx + PadX, by + PadY));
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
