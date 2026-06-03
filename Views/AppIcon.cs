using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MaplePet.Views;

/// <summary>
/// The app's mushroom icon (<c>Assets/Program/icon.png</c>), loaded once from the embedded Avalonia
/// resources and reused for the tray icon, the config window's taskbar icon, and its title-bar logo.
/// The executable's own icon is the multi-resolution <c>icon.ico</c> wired via &lt;ApplicationIcon&gt;.
/// </summary>
internal static class AppIcon
{
    private static readonly Uri PngUri = new("avares://MaplePet/Assets/Program/icon.png");
    private static readonly Uri IcoUri = new("avares://MaplePet/Assets/Program/icon.ico");
    private static Bitmap? _bitmap;
    private static bool _tried;

    /// <summary>The 512px icon bitmap (for in-app <c>Image</c>s and native-menu items), or null if it
    /// can't be loaded. Cached for the app's lifetime.</summary>
    public static Bitmap? Bitmap
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                try { _bitmap = new Bitmap(AssetLoader.Open(PngUri)); }
                catch { _bitmap = null; }
            }
            return _bitmap;
        }
    }

    /// <summary>A <see cref="WindowIcon"/> for the window/taskbar and tray icons. Built from the
    /// multi-resolution .ico so Win32 selects a purpose-rendered frame for each requested size
    /// (taskbar ~24–48px, tray ~16–24px) instead of runtime-scaling one large bitmap. Falls back to
    /// the 512px PNG, then null.</summary>
    public static WindowIcon? WindowIcon()
    {
        try { return new WindowIcon(AssetLoader.Open(IcoUri)); }
        catch
        {
            try { return Bitmap is { } b ? new WindowIcon(b) : null; }
            catch { return null; }
        }
    }
}
