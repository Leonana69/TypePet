using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TypePet.Platform.Abstractions;

/// <summary>The icon shown beside a tray/menu-bar item.</summary>
public enum TrayGlyph { Contact, Settings, Power, Chat }

/// <summary>
/// Produces the small bitmaps used for tray menu item icons. Keeps App.axaml.cs free of OS-specific
/// font/asset assumptions (Windows historically rasterized Segoe Fluent / MDL2 glyphs, which don't
/// exist on macOS). Returns null when the item should fall back to a text-only entry.
/// </summary>
public interface ITrayGlyphs
{
    Bitmap? Render(TrayGlyph glyph, Color color);
}
