using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TypePet.Views;

/// <summary>
/// Dev-only optical-centering probe for the pill-style buttons (the Browse tab's Install / Remove /
/// Update actions). Renders the styled buttons offscreen — no window is shown — then scans the pixels
/// to measure where the label ink actually sits inside the pill. Avalonia centers the font *line box*;
/// for Segoe UI the line-box center sits above the visual glyph center, so perfectly symmetric padding
/// leaves the label looking low. Prints per-button vertical offsets (+ = label sits LOW) and saves
/// 1x/4x PNGs for eyeballing. Invoked from <see cref="Program"/> via <c>--button-align-test</c>; the
/// caller runs <c>SetupWithoutStarting()</c> first.
/// </summary>
public static class ButtonAlignTest
{
    private const byte BgR = 0x10, BgG = 0x1C, BgB = 0x1F;

    public static int Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        // The hub row's action buttons, exactly as HubView builds them (style class + FontSize 11).
        (string Cls, string Text)[] cases =
        {
            ("accent", "Install"),
            ("danger", "Remove"),
            ("accent", "Update → 0.2.0"),
            ("ghost", "Needs app ≥ 1.2.3"),
        };

        var panel = new StackPanel { Spacing = 20, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (cls, text) in cases)
        {
            var b = new Button { Content = text, FontSize = 11 };
            b.Classes.Add(cls);
            panel.Children.Add(b);
        }

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(BgR, BgG, BgB)),
            Padding = new Thickness(24),
            Child = panel,
        };

        // App-level styles only apply inside a rooted tree, so host in a window (never shown) and lay
        // out manually; RenderTargetBitmap renders the subtree without needing the compositor.
        var window = new Window { Content = root, ShowInTaskbar = false };
        window.Measure(new Size(1200, 1200));
        window.Arrange(new Rect(window.DesiredSize));

        int w = (int)Math.Ceiling(root.Bounds.Width);
        int h = (int)Math.Ceiling(root.Bounds.Height);

        string outDir = Path.Combine(Path.GetTempPath(), "TypePet_btnalign");
        Directory.CreateDirectory(outDir);

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(root);
        string png1 = Path.Combine(outDir, "buttons_1x.png");
        rtb.Save(png1);

        var rtb4 = new RenderTargetBitmap(new PixelSize(w * 4, h * 4), new Vector(384, 384));
        rtb4.Render(root);
        string png4 = Path.Combine(outDir, "buttons_4x.png");
        rtb4.Save(png4);

        Console.WriteLine($"saved {png1}");
        Console.WriteLine($"saved {png4}");
        Console.WriteLine($"font: {FontManager.Current.DefaultFontFamily}");
        foreach (Button b in panel.Children)
            Console.WriteLine($"  '{b.Content}'  size {b.Bounds.Size}");
        Console.WriteLine();

        var px = new byte[w * 4 * h];
        nint buf = Marshal.AllocHGlobal(px.Length);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, w, h), buf, px.Length, w * 4);
            Marshal.Copy(buf, px, 0, px.Length);
        }
        finally { Marshal.FreeHGlobal(buf); }

        Analyze(px, w, h, cases);
        return 0;
    }

    private static void Analyze(byte[] px, int w, int h, (string Cls, string Text)[] cases)
    {
        (byte r, byte g, byte b) P(int x, int y) => (px[(y * w + x) * 4 + 2], px[(y * w + x) * 4 + 1], px[(y * w + x) * 4]);
        bool IsBg((byte r, byte g, byte b) c) => Math.Abs(c.r - BgR) <= 12 && Math.Abs(c.g - BgG) <= 12 && Math.Abs(c.b - BgB) <= 12;

        // Segment the image into one horizontal band per button (rows containing non-background pixels).
        var rowHas = new bool[h];
        for (int y = 0; y < h; y++)
        {
            int n = 0;
            for (int x = 0; x < w && n < 2; x++) if (!IsBg(P(x, y))) n++;
            rowHas[y] = n >= 2;
        }
        var bands = new List<(int Top, int Bottom)>();
        for (int y = 0; y < h; y++)
        {
            if (!rowHas[y]) continue;
            int top = y;
            while (y + 1 < h && rowHas[y + 1]) y++;
            bands.Add((top, y));
        }

        if (bands.Count != cases.Length)
        {
            Console.WriteLine($"!! expected {cases.Length} bands, found {bands.Count} — analysis skipped, check the PNG");
            return;
        }

        Console.WriteLine("button                pill rows       ink rows        bboxΔ   centroidΔ   (+ = label sits LOW)");
        for (int i = 0; i < bands.Count; i++)
        {
            var (top, bot) = bands[i];
            string cls = cases[i].Cls;

            // Pill x-extent, so ink sampling stays clear of the rounded sides and the 1px theme border.
            int left = w, right = 0;
            for (int y = top; y <= bot; y++)
                for (int x = 0; x < w; x++)
                    if (!IsBg(P(x, y))) { if (x < left) left = x; if (x > right) right = x; }

            // The G channel separates pill fill from label ink in every class. accent = dark-on-teal,
            // danger/ghost = bright-on-dark.
            int pillG = cls switch { "accent" => 201, "danger" => 72, _ => 56 };
            bool darkInk = cls == "accent";
            int fullContrast = darkInk ? pillG - 32 : 255 - pillG;

            double sumW = 0, sumWy = 0;
            int inkTop = -1, inkBot = -1;
            for (int y = top + 2; y <= bot - 2; y++)
            {
                for (int x = left + 6; x <= right - 6; x++)
                {
                    var c = P(x, y);
                    int contrast = darkInk ? pillG - c.g : c.g - pillG;
                    if (contrast <= 8) continue;
                    sumW += contrast; sumWy += contrast * (y + 0.5);
                    if (contrast * 2 >= fullContrast) { if (inkTop < 0) inkTop = y; inkBot = y; }
                }
            }

            double pillC = (top + bot + 1) / 2.0;
            double bboxC = inkTop >= 0 ? (inkTop + inkBot + 1) / 2.0 : double.NaN;
            double inkC = sumW > 0 ? sumWy / sumW : double.NaN;
            Console.WriteLine(
                $"{cases[i].Text,-20}  {top,3}..{bot,-3} ({bot - top + 1,2}px)  {inkTop,3}..{inkBot,-3} ({(inkTop >= 0 ? inkBot - inkTop + 1 : 0),2}px)  " +
                $"{bboxC - pillC,6:+0.00;-0.00;0.00}  {inkC - pillC,6:+0.00;-0.00;0.00}");
        }
    }
}
