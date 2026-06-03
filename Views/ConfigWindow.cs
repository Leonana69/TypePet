using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MaplePet.Views;

public enum ConfigTab { Characters, Settings }

/// <summary>
/// The single frosted-glass config window opened from the tray. It hosts the Characters and Settings
/// tabs (<see cref="CharacterView"/> / <see cref="SettingsView"/>) under a segmented tab bar; both
/// views are kept alive so switching tabs preserves their state. The tray's "Characters…" and
/// "Settings…" items open this one window on the matching tab.
/// </summary>
public sealed class ConfigWindow : FrostedWindow
{
    private readonly CharacterView _charactersView;
    private readonly SettingsView _settingsView;
    private readonly ContentControl _host;
    private readonly Button _tabCharacters, _tabSettings;

    public ConfigWindow(CharacterView charactersView, SettingsView settingsView) : base("MaplePet")
    {
        _charactersView = charactersView;
        _settingsView = settingsView;

        // Sized to fit the 6-wide character grid; the Settings tab's narrower content sits left-aligned.
        Width = 660;
        Height = 600;

        _host = new ContentControl();

        _tabCharacters = TabButton("Characters", ConfigTab.Characters);
        _tabSettings = TabButton("Settings", ConfigTab.Settings);
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(10, 0, 0, 0),
            Children = { _tabCharacters, _tabSettings },
        };
        var tabBar = new Border
        {
            BorderBrush = FrostTheme.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tabs,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(tabBar, Dock.Top);
        dock.Children.Add(tabBar);
        dock.Children.Add(_host);
        SetBody(dock);

        Select(ConfigTab.Characters);
    }

    /// <summary>The Settings tab — exposed so the app can push tray-driven overlay changes into it.</summary>
    public SettingsView SettingsView => _settingsView;

    /// <summary>Show the given tab.</summary>
    public void Select(ConfigTab tab)
    {
        bool chars = tab == ConfigTab.Characters;
        _host.Content = chars ? _charactersView : (Control)_settingsView;
        SetActive(_tabCharacters, chars);
        SetActive(_tabSettings, !chars);
    }

    protected override void OnClosed(EventArgs e)
    {
        _charactersView.DisposeThumbnails();
        base.OnClosed(e);
    }

    private Button TabButton(string text, ConfigTab tab)
    {
        var b = new Button { Content = text };
        b.Classes.Add("tab");
        b.Click += (_, _) => Select(tab);
        return b;
    }

    private static void SetActive(Button tab, bool active)
    {
        tab.Classes.Remove("tabActive");
        if (active) tab.Classes.Add("tabActive");
    }
}
