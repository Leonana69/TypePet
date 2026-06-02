using System;
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
    private SettingsWindow? _settingsWindow;
    private CharacterWindow? _characterWindow;

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

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var charactersItem = new NativeMenuItem("Characters…");
        charactersItem.Click += (_, _) => ShowCharacters();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowSettings();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(charactersItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = CreateTrayIcon(),
            ToolTipText = "MaplePet",
            Menu = menu,
            IsVisible = true,
        };

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void ShowSettings()
    {
        if (_settings is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ShowCharacters()
    {
        if (_settings is null || _store is null) return;
        if (_characterWindow is not null)
        {
            _characterWindow.Activate();
            return;
        }
        _characterWindow = new CharacterWindow(_store, _settings, ApplyCharacter);
        _characterWindow.Closed += (_, _) => _characterWindow = null;
        _characterWindow.Show();
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
    }

    /// <summary>Render a small rounded blue square (matching the pet) to use as the tray icon.</summary>
    private static WindowIcon? CreateTrayIcon()
    {
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawRectangle(
                    new SolidColorBrush(Color.FromRgb(66, 135, 245)),
                    new Pen(new SolidColorBrush(Color.FromRgb(20, 40, 90)), 4),
                    new Avalonia.Rect(8, 8, 48, 48), 12, 12);
                ctx.DrawRectangle(Brushes.White, null, new Avalonia.Rect(40, 22, 9, 9));
            }
            return new WindowIcon(rtb);
        }
        catch
        {
            return null;
        }
    }
}
