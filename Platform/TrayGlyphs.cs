using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform;

/// <summary>
/// Cross-platform tray/menu-bar glyphs, drawn as simple vector shapes so they look identical and crisp
/// on Windows and macOS without depending on a system icon font (the old Windows path rasterized Segoe
/// Fluent / MDL2 glyphs, which don't exist on macOS).
///
/// <para>On Windows the tray popup is a per-pixel-alpha window (Avalonia's TrayPopupRoot) and small
/// transparent bitmaps lose their alpha when composited into it — rendering an opaque black box behind
/// the glyph. So on Windows the glyph is drawn on an OPAQUE menu-surface background (the exact dark
/// Fluent menu colour, so the tile is invisible). On macOS NSMenu composites alpha correctly, so the
/// glyph is drawn on a transparent background and tinted by <paramref>color</paramref>.</para>
/// </summary>
public sealed class TrayGlyphs : ITrayGlyphs
{
    private readonly bool _opaqueBackground;
    private readonly Color _surface;

    /// <param name="opaqueBackground">Windows: true (draw on <paramref name="surface"/> to dodge the
    /// per-pixel-alpha black box). macOS: false (transparent, system tints it).</param>
    /// <param name="surface">The opaque menu-surface colour to draw behind the glyph on Windows.</param>
    public TrayGlyphs(bool opaqueBackground, Color surface)
    {
        _opaqueBackground = opaqueBackground;
        _surface = surface;
    }

    public Bitmap? Render(TrayGlyph glyph, Color color)
    {
        try
        {
            const int size = 32;
            using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Antialias }))
            {
                if (_opaqueBackground)
                    ctx.DrawRectangle(new SolidColorBrush(_surface), null, new Rect(0, 0, size, size));

                var brush = new SolidColorBrush(color);
                switch (glyph)
                {
                    case TrayGlyph.Contact: DrawPerson(ctx, brush); break;
                    case TrayGlyph.Settings: DrawSliders(ctx, brush); break;
                    case TrayGlyph.Power: DrawPower(ctx, brush); break;
                }
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null; // a null Icon renders the menu item as text-only — acceptable
        }
    }

    // All shapes are authored on a 32x32 canvas.

    /// <summary>A person bust: a head circle above a filled shoulders curve.</summary>
    private static void DrawPerson(DrawingContext ctx, IBrush brush)
    {
        ctx.DrawEllipse(brush, null, new Point(16, 11), 4.6, 4.6);

        var shoulders = new StreamGeometry();
        using (var g = shoulders.Open())
        {
            g.BeginFigure(new Point(7.5, 25), isFilled: true);
            g.CubicBezierTo(new Point(7.5, 19.5), new Point(11, 17.5), new Point(16, 17.5));
            g.CubicBezierTo(new Point(21, 17.5), new Point(24.5, 19.5), new Point(24.5, 25));
            g.LineTo(new Point(7.5, 25));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, shoulders);
    }

    /// <summary>A "tune"/settings icon: three horizontal slider tracks each with a round handle.</summary>
    private static void DrawSliders(DrawingContext ctx, IBrush brush)
    {
        var pen = new Pen(brush, 2.2) { LineCap = PenLineCap.Round };
        ReadOnlySpan<double> ys = [10, 16, 22];
        ReadOnlySpan<double> knobX = [20, 12, 21];
        for (int i = 0; i < 3; i++)
        {
            ctx.DrawLine(pen, new Point(7, ys[i]), new Point(25, ys[i]));
            ctx.DrawEllipse(brush, null, new Point(knobX[i], ys[i]), 3, 3);
        }
    }

    /// <summary>A power symbol: a near-full ring with a gap at the top, plus a vertical stem.</summary>
    private static void DrawPower(DrawingContext ctx, IBrush brush)
    {
        var pen = new Pen(brush, 2.4) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var ring = new StreamGeometry();
        using (var g = ring.Open())
        {
            // ~290° arc centred so the ~70° gap sits at the top (12 o'clock).
            g.BeginFigure(new Point(20.59, 10.45), isFilled: false);
            g.ArcTo(new Point(11.41, 10.45), new Size(8, 8), 0, isLargeArc: true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, ring);
        ctx.DrawLine(pen, new Point(16, 8), new Point(16, 16));
    }
}
