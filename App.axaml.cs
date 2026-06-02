using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MaplePet.Engine;
using MaplePet.Views;

namespace MaplePet;

public partial class App : Application
{
    private Settings? _settings;
    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _settings = Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"));
            desktop.MainWindow = new PetWindow(_settings);
            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowSettings();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
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
