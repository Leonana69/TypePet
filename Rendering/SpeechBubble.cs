using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace MaplePet.Rendering;

/// <summary>
/// Draws a small speech bubble above (or, if there's no room, below) the pet — the control API's
/// <c>Say</c> channel. Text wraps to a max width; the bubble is clamped to stay on screen and a tail
/// points at the pet. The palette mirrors <c>Views/FrostTheme</c> so it matches the config UI.
/// </summary>
public static class SpeechBubble
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0xF2, 0x0E, 0x17, 0x1B)); // SurfaceTint
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xF2));    // TextPrimary
    private static readonly IPen Border = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x20, 0xC9, 0xC2)), 1); // Accent

    private const double MaxTextWidth = 240;
    private const double PadX = 10, PadY = 7;
    private const double Radius = 9;
    private const double TailW = 12, TailH = 9;
    private const double GapToPet = 6;
    private const double FontSize = 13;

    /// <summary>Draw <paramref name="text"/> in a bubble whose tail points at (<paramref name="anchorX"/>,
    /// <paramref name="topY"/>) — the top-center of the drawn pet — clamped within <paramref name="screen"/>.</summary>
    public static void Draw(DrawingContext ctx, string text, double anchorX, double topY, MaplePet.Engine.Rect screen)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, FontSize, TextBrush) { MaxTextWidth = MaxTextWidth };

        double w = Math.Ceiling(ft.Width) + PadX * 2;
        double h = Math.Ceiling(ft.Height) + PadY * 2;

        // Prefer above the pet (tail pointing down). If it would clip the top edge, place it below.
        bool below = topY - GapToPet - TailH - h < screen.Top + 4;
        double bx = Math.Clamp(anchorX - w / 2, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - w - 4));
        double by = below ? topY + GapToPet + TailH : topY - GapToPet - TailH - h;

        var rect = new Avalonia.Rect(bx, by, w, h);
        ctx.DrawRectangle(Fill, Border, rect, Radius, Radius);

        // Tail triangle, base on the bubble edge nearest the pet, apex pointing at the pet.
        double tailX = Math.Clamp(anchorX, bx + Radius + TailW / 2, bx + w - Radius - TailW / 2);
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            double baseY = below ? rect.Top + 0.5 : rect.Bottom - 0.5;
            double apexY = below ? baseY - TailH : baseY + TailH;
            g.BeginFigure(new Point(tailX - TailW / 2, baseY), true);
            g.LineTo(new Point(tailX + TailW / 2, baseY));
            g.LineTo(new Point(tailX, apexY));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Fill, null, tail);

        ctx.DrawText(ft, new Point(bx + PadX, by + PadY));
    }
}
