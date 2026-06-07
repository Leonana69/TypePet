using System;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MaplePet.Views;

/// <summary>
/// Dev-only smoke test for <see cref="ChatMarkup"/>. Renders a few chat-reply strings through the real
/// parse path and dumps the resulting inlines (run text + bold/italic flags, and link display/href) plus
/// the spoken (emphasis-stripped) form, so the bold/italic + clickable-link behaviour can be eyeballed
/// without launching the UI. Invoked from <see cref="Program"/> via <c>--markup-test</c>; needs Avalonia's
/// asset/control setup, so the caller runs <c>SetupWithoutStarting()</c> first. Exits without showing a window.
/// </summary>
public static class MarkupTest
{
    public static int Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* output may be redirected */ }

        string[] cases =
        {
            "Sure do! The Aran class Discord invite is **discord.gg/WpJ4VDta8V** — straight from the Grandis Library guide page. It's a great place to go for in-depth tips and community help!",
            "Visit https://grandislibrary.com/explorers/hero for the full build.",
            "Try **bold**, *italic*, and ***both*** together.",
            "Check www.example.com today, e.g. at 3 p.m.",
            "Hero's link skill is **Invincible Belief** — see mapleskill.com/jobs/hero/ too.",
        };

        foreach (var text in cases)
        {
            Console.WriteLine("INPUT : " + text);
            Console.WriteLine("SPOKEN: " + ChatMarkup.ToSpoken(text));
            var tb = ChatMarkup.BuildBlock(text, Brushes.White, url => { });
            Console.WriteLine("INLINES:");
            foreach (var inline in tb.Inlines!)
            {
                switch (inline)
                {
                    case Run r:
                        Console.WriteLine($"  run{Style(r.FontWeight, r.FontStyle)}: \"{r.Text}\"");
                        break;
                    case InlineUIContainer { Child: TextBlock lt }:
                        Console.WriteLine($"  LINK{Style(lt.FontWeight, lt.FontStyle)}: \"{lt.Text}\" -> {ToolTip.GetTip(lt)}");
                        break;
                    default:
                        Console.WriteLine($"  ?? {inline.GetType().Name}");
                        break;
                }
            }
            Console.WriteLine();
        }
        return 0;
    }

    private static string Style(FontWeight w, FontStyle s)
    {
        string flags = (w == FontWeight.Bold ? "b" : "") + (s == FontStyle.Italic ? "i" : "");
        return flags.Length > 0 ? $"[{flags}]" : "";
    }
}
