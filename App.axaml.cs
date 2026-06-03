using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MaplePet.Engine;
using MaplePet.Rendering;
using MaplePet.Views;

namespace MaplePet;

public partial class App : Application
{
    private Settings? _settings;
    private CharacterStore? _store;
    private PetWindow? _petWindow;
    private TrayIcon? _trayIcon;
    private ConfigWindow? _configWindow;

    // Tray-menu items we keep refreshing as state changes elsewhere.
    private NativeMenuItem? _wearingItem; // "MaplePet — <character>" header
    private NativeMenuItem? _overlayItem; // live "Show debug overlay" checkbox

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dev-only: render the character poses to PNGs and exit (no overlay/tray).
            if (AppState.RenderPosesDir is string dir)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { MaplePet.Rendering.PoseRenderTest.Run(dir, AppState.RenderPosesFrom); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "ERROR.txt"), ex.ToString()); }
                    desktop.Shutdown();
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _settings = Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"));
            _store = new CharacterStore();

            // If the remembered character's folder is gone (deleted out-of-band while closed), fall
            // back to the default and persist it, so the pet and the picker's highlight agree.
            if (_store.Get(_settings.CurrentCharacterId) is null)
            {
                _settings.CurrentCharacterId = CharacterStore.DefaultId;
                _settings.Save();
            }

            _petWindow = new PetWindow(_settings, _store);
            desktop.MainWindow = _petWindow;
            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Segoe Fluent Icons / MDL2 glyph codepoints (present on Windows 10/11) for the tray menu items.
    // Stored as ints so no Private-Use-Area chars live in the source file.
    private const int GlyphContact = 0xE77B;  // person bust
    private const int GlyphSettings = 0xE713;  // gear
    private const int GlyphView = 0xE7B3;      // eye
    private const int GlyphPower = 0xE7E8;      // power button

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var accent = FrostTheme.Accent;

        // A non-clickable header that names the app and the worn character.
        _wearingItem = new NativeMenuItem(WearingLabel())
        {
            Icon = CreateAppGlyph(),
            IsEnabled = false,
        };

        var charactersItem = new NativeMenuItem("Characters…") { Icon = RenderGlyph(GlyphContact, accent) };
        charactersItem.Click += (_, _) => ShowCharacters();

        var settingsItem = new NativeMenuItem("Settings…") { Icon = RenderGlyph(GlyphSettings, accent) };
        settingsItem.Click += (_, _) => ShowSettings();

        _overlayItem = new NativeMenuItem("Show debug overlay")
        {
            Icon = RenderGlyph(GlyphView, accent),
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _settings?.ShowOverlay ?? false,
        };
        _overlayItem.Click += (_, _) => ToggleOverlay();

        var exitItem = new NativeMenuItem("Exit") { Icon = RenderGlyph(GlyphPower, FrostTheme.Exit) };
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(_wearingItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(charactersItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_overlayItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        // Belt-and-suspenders resync when the menu opens. NOTE: the Win32 tray backend never raises
        // NativeMenu.Opening (only macOS does), so on Windows this is a no-op — the authoritative sync
        // is the explicit pushes in ToggleOverlay (tray->Settings), the SettingsView overlay callback
        // (Settings->tray), and ApplyCharacter / the picker's onChanged callback (character->header).
        menu.Opening += (_, _) =>
        {
            if (_settings is not null && _overlayItem is not null)
                _overlayItem.IsChecked = _settings.ShowOverlay;
            if (_wearingItem is not null)
                _wearingItem.Header = WearingLabel();
        };

        _trayIcon = new TrayIcon
        {
            Icon = CreateTrayIcon(),
            ToolTipText = "MaplePet",
            Menu = menu,
            IsVisible = true,
        };

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    /// <summary>Flip the debug overlay from the tray. The pet window re-reads ShowOverlay each poll
    /// tick, so this takes effect within a frame or two without a restart.</summary>
    private void ToggleOverlay()
    {
        if (_settings is null) return;
        _settings.ShowOverlay = !_settings.ShowOverlay;
        if (_overlayItem is not null) _overlayItem.IsChecked = _settings.ShowOverlay;
        _settings.Save();
        // Keep an open Settings tab's switch in sync, else its next edit writes the stale value back.
        _configWindow?.SettingsView.SetOverlay(_settings.ShowOverlay);
    }

    private string WearingLabel()
    {
        var name = _store?.Get(_settings?.CurrentCharacterId ?? CharacterStore.DefaultId)?.DisplayName ?? "Default";
        return $"MaplePet — {name}";
    }

    private void ShowSettings() => ShowConfig(ConfigTab.Settings);
    private void ShowCharacters() => ShowConfig(ConfigTab.Characters);

    /// <summary>Open (or re-focus) the single config window on the requested tab.</summary>
    private void ShowConfig(ConfigTab tab)
    {
        if (_settings is null || _store is null) return;
        if (_configWindow is not null)
        {
            _configWindow.Select(tab);
            _configWindow.Activate();
            return;
        }

        var settingsView = new SettingsView(_settings, b =>
        {
            if (_overlayItem is not null) _overlayItem.IsChecked = b; // mirror into the tray checkbox
        });
        var charactersView = new CharacterView(_store, _settings, ApplyCharacter, () =>
        {
            if (_wearingItem is not null) _wearingItem.Header = WearingLabel(); // refresh after a rename
        });
        _configWindow = new ConfigWindow(charactersView, settingsView);
        _configWindow.Select(tab);
        _configWindow.Closed += (_, _) => _configWindow = null;
        _configWindow.Show();
        _configWindow.Activate();
    }

    /// <summary>Make the pet wear the given character and remember the choice. Invoked by the
    /// character picker when a card is selected, a new import is added, or the worn one is deleted.</summary>
    private void ApplyCharacter(string id)
    {
        if (_settings is null || _store is null || _petWindow is null) return;

        var sprites = CharacterLoader.Load(_store, id, CharacterAnimator.ActivePoses);
        _petWindow.SetCharacter(sprites);
        _settings.CurrentCharacterId = id;
        _settings.Save();

        // Keep the tray header's "wearing" line current.
        if (_wearingItem is not null) _wearingItem.Header = WearingLabel();
    }

    /// <summary>Render a small rounded teal square (matching the pet's accent) for the tray icon.</summary>
    private static WindowIcon? CreateTrayIcon()
    {
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawRectangle(
                    new SolidColorBrush(FrostTheme.Accent),
                    new Pen(new SolidColorBrush(FrostTheme.AccentDim), 4),
                    new Avalonia.Rect(8, 8, 48, 48), 14, 14);
                ctx.DrawRectangle(Brushes.White, null, new Avalonia.Rect(40, 22, 9, 9));
            }
            return new WindowIcon(rtb);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The small teal rounded-square app glyph used as the tray-menu header icon.</summary>
    private static Bitmap? CreateAppGlyph()
    {
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawRectangle(
                    new SolidColorBrush(FrostTheme.Accent), null,
                    new Avalonia.Rect(5, 5, 22, 22), 7, 7);
                ctx.DrawRectangle(Brushes.White, null, new Avalonia.Rect(19, 11, 5, 5));
            }
            return rtb;
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

    /// <summary>Rasterize a Segoe Fluent Icons / MDL2 glyph (by codepoint) into a tinted bitmap for a
    /// native menu item. Returns null when no installed icon font actually contains the glyph, so the
    /// menu item shows no icon rather than a ".notdef" tofu box (Avalonia silently substitutes a
    /// fallback font and renders glyph 0 instead of throwing).</summary>
    private static Bitmap? RenderGlyph(int codepoint, Color color, int size = 32)
    {
        try
        {
            if (!TryGetIconTypeface((uint)codepoint, out var typeface))
                return null;

            var text = new FormattedText(
                char.ConvertFromUtf32(codepoint),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * 0.66,
                new SolidColorBrush(color));

            var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                var origin = new Point((size - text.Width) / 2, (size - text.Height) / 2);
                ctx.DrawText(text, origin);
            }
            return rtb;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Find an installed icon font that actually contains <paramref name="codepoint"/> (glyph
    /// index != 0), so we can decline to draw rather than emit a tofu box on a font-less machine.</summary>
    private static bool TryGetIconTypeface(uint codepoint, out Typeface typeface)
    {
        foreach (var family in IconFonts)
        {
            var tf = new Typeface(new FontFamily(family));
            if (FontManager.Current.TryGetGlyphTypeface(tf, out var gt) &&
                gt.TryGetGlyph(codepoint, out var gid) && gid != 0)
            {
                typeface = tf;
                return true;
            }
        }
        typeface = default!;
        return false;
    }
}
