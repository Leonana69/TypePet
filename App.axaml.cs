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
    private SayBarWindow? _sayBar;
    private MaplePet.Api.Mcp.PetMcpServer? _mcpServer;

    // The tray header ("MaplePet — <character>"), refreshed when the worn character changes.
    private NativeMenuItem? _wearingItem;

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

            // Pop up the floating say-input bar when the pet is double-clicked or the global hotkey fires.
            _petWindow.SayInputRequested += ShowSayInput;

            // Optionally expose the pet over a local MCP tool server so an LLM/agent can drive it.
            // Started once the control facade is live (end of PetWindow.OnOpened); off by default.
            if (_settings.EnableMcpServer)
            {
                int port = _settings.McpPort;
                _petWindow.ControlReady += () =>
                {
                    if (_petWindow?.Control is { } control)
                        _mcpServer = MaplePet.Api.Mcp.PetMcpServer.Start(control, port);
                };
                desktop.Exit += (_, _) => _mcpServer?.Stop();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Segoe Fluent Icons / MDL2 glyph codepoints (present on Windows 10/11) for the tray menu items.
    // Stored as ints so no Private-Use-Area chars live in the source file.
    private const int GlyphContact = 0xE77B;  // person bust
    private const int GlyphSettings = 0xE713;  // gear
    private const int GlyphPower = 0xE7E8;      // power button

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var accent = FrostTheme.Accent;

        // A non-clickable header that names the app and the worn character.
        _wearingItem = new NativeMenuItem(WearingLabel())
        {
            Icon = AppIcon.Bitmap,
            IsEnabled = false,
        };

        var charactersItem = new NativeMenuItem("Characters…") { Icon = RenderGlyph(GlyphContact, accent) };
        charactersItem.Click += (_, _) => ShowCharacters();

        var settingsItem = new NativeMenuItem("Settings…") { Icon = RenderGlyph(GlyphSettings, accent) };
        settingsItem.Click += (_, _) => ShowSettings();

        var exitItem = new NativeMenuItem("Exit") { Icon = RenderGlyph(GlyphPower, FrostTheme.Exit) };
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(_wearingItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(charactersItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        // Belt-and-suspenders header refresh when the menu opens (a no-op on Windows: the Win32 tray
        // backend never raises NativeMenu.Opening — only macOS does). The header is also kept current
        // by ApplyCharacter and the picker's onChanged callback.
        menu.Opening += (_, _) =>
        {
            if (_wearingItem is not null)
                _wearingItem.Header = WearingLabel();
        };

        _trayIcon = new TrayIcon
        {
            Icon = AppIcon.WindowIcon(),
            ToolTipText = AppInfo.NameAndVersion,
            Menu = menu,
            IsVisible = true,
        };

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
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

        // (true) while the user is capturing a hotkey -> suspend the live hook so it doesn't swallow the
        // very chord being captured; (false) when done -> re-arm with the (possibly new) saved gesture.
        var settingsView = new SettingsView(_settings, capturing =>
        {
            if (capturing) _petWindow?.SuspendSayHotkey();
            else _petWindow?.SetSayHotkey(_settings.SayInputHotkey);
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

    /// <summary>Open (or re-focus) the floating say-input bar; what the user types is spoken by the
    /// pet via the control API. Invoked by a pet double-click or the global hotkey.</summary>
    private void ShowSayInput()
    {
        if (_petWindow is null) return;
        if (_sayBar is not null)
        {
            _sayBar.Activate();
            _sayBar.FocusInput();
            return;
        }

        _petWindow.SuppressOverlayTopmost = true; // keep the bar above the (topmost) pet overlay while open
        _sayBar = new SayBarWindow(text => _petWindow?.Control?.Say(text));
        _sayBar.Closed += (_, _) =>
        {
            _sayBar = null;
            if (_petWindow is not null) _petWindow.SuppressOverlayTopmost = false;
        };
        _sayBar.Show();
        _sayBar.Activate();

        // The double-click that summons the bar is swallowed by the mouse hook, so Windows can refuse
        // the initial foreground activation; re-assert focus once the window exists.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _sayBar?.Activate();
            _sayBar?.FocusInput();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>Make the pet wear the given character and remember the choice. Invoked by the
    /// character picker when a card is selected, a new import is added, or the worn one is deleted.</summary>
    private void ApplyCharacter(string id)
    {
        if (_settings is null || _store is null || _petWindow is null) return;

        var sprites = CharacterLoader.Load(_store, id,
            hitTestPoses: CharacterAnimator.ActivePoses, posesToLoad: CharacterAnimator.LivePoses, loadExpressions: true);
        _petWindow.SetCharacter(sprites);
        _settings.CurrentCharacterId = id;
        _settings.Save();

        // Keep the tray header's "wearing" line current.
        if (_wearingItem is not null) _wearingItem.Header = WearingLabel();
    }

    private static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

    // The dark Fluent menu surface the tray popup paints behind each item (measured: solid #2B2B2B).
    // The app forces RequestedThemeVariant=Dark, so this is stable regardless of the Windows light/dark
    // setting. Glyph icons are rendered ONTO this exact colour (see RenderGlyph) so the icon tile is
    // invisible against the menu.
    private static readonly Color TrayMenuSurface = Color.FromRgb(0x2B, 0x2B, 0x2B);

    /// <summary>Rasterize a Segoe Fluent Icons / MDL2 glyph (by codepoint) into a tinted bitmap for a
    /// native menu item. Returns null when no installed icon font actually contains the glyph, so the
    /// menu item shows no icon rather than a ".notdef" tofu box (Avalonia silently substitutes a
    /// fallback font and renders glyph 0 instead of throwing).</summary>
    /// <remarks>
    /// The glyph is drawn on an OPAQUE background (<see cref="TrayMenuSurface"/>), NOT a transparent one,
    /// and at a SMALL size. Both matter, and were verified in the live tray popup:
    /// <list type="bullet">
    /// <item>A small TRANSPARENT glyph bitmap renders an opaque BLACK box behind the glyph — the tray
    /// popup is a transparent per-pixel-alpha window (Avalonia's TrayPopupRoot) and small
    /// render-target-derived bitmaps lose their alpha when composited into it. A fully OPAQUE bitmap has
    /// no alpha to lose, so no box; and because its background is the exact menu colour the tile is
    /// invisible (only faintly visible under the row's hover highlight).</item>
    /// <item>Rendering small (≈ the ~16–24px display size) keeps the thin 1px strokes crisp. Rendering
    /// large and letting the menu downscale heavily thins/breaks them (256px → the ring became
    /// disconnected arcs; even 96px dropped pixels).</item>
    /// </list>
    /// Grayscale text AA (not the default subpixel) avoids the coloured LCD fringing that drawing text on
    /// an opaque background otherwise produces. The result is baked into an immutable decoded bitmap so
    /// the menu doesn't hold a live (disposable) render target.
    /// </remarks>
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

            using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushRenderOptions(new RenderOptions { TextRenderingMode = TextRenderingMode.Antialias }))
            {
                ctx.DrawRectangle(new SolidColorBrush(TrayMenuSurface), null, new Avalonia.Rect(0, 0, size, size));
                var origin = new Point((size - text.Width) / 2, (size - text.Height) / 2);
                ctx.DrawText(text, origin);
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            return new Bitmap(ms);
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
