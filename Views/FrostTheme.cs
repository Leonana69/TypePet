using Avalonia.Media;

namespace TypePet.Views;

/// <summary>
/// The "frosted glass" palette, mirrored from <c>App.axaml</c> for the bits of UI built in
/// code-behind (the acrylic material, card thumbnails, status text, tray glyphs). Keep these values
/// in sync with the brushes/colors declared in App.axaml.
/// </summary>
internal static class FrostTheme
{
    public static readonly Color Accent = Color.FromRgb(0x20, 0xC9, 0xC2);
    public static readonly Color AccentDim = Color.FromRgb(0x15, 0x91, 0x8C);
    public static readonly Color Exit = Color.FromRgb(0xE0, 0x6A, 0x6C);

    // Acrylic surface: a near-black teal the blur is tinted with (and the solid fallback when the
    // platform can't do real acrylic).
    public static readonly Color SurfaceTint = Color.FromRgb(0x0E, 0x17, 0x1B);
    public static readonly Color SurfaceFallback = Color.FromRgb(0x12, 0x1C, 0x20);

    public static readonly IBrush TextPrimary = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xF2));
    public static readonly IBrush TextSecondary = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xA5));
    public static readonly IBrush Hairline = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush ChromeBorder = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

    // Subtle inset behind a card's sprite thumbnail.
    public static readonly IBrush ThumbWell = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

    public static readonly IBrush StatusError = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
    public static readonly IBrush StatusOk = new SolidColorBrush(Color.FromRgb(0x4F, 0xD6, 0xC9));
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    public static readonly IBrush BadgeText = new SolidColorBrush(Color.FromRgb(0x04, 0x20, 0x1F));
}
