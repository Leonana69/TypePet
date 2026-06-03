using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MaplePet.Engine;
using MaplePet.Platform;

namespace MaplePet.Views;

/// <summary>
/// The Settings tab of <see cref="ConfigWindow"/>: the configurable parameters grouped into Movement /
/// Behavior / Advanced / System sections. Every change applies immediately — the controller reads
/// <see cref="Settings"/> each tick — and is persisted to settings.json on the spot, so there is no
/// Save button. Target FPS / poll rate only take effect on the next launch. "Start with Windows" is
/// backed by the registry (see <see cref="StartupRegistration"/>), not settings.json.
/// </summary>
public sealed class SettingsView : UserControl
{
    private readonly Settings _cfg;
    private readonly NumericUpDown _jump, _roam, _walk, _climb, _gravity, _fps, _poll;
    private readonly ToggleSwitch _overlay, _startup;
    private bool _ready; // suppress change handlers while the initial values are being set

    public SettingsView(Settings cfg)
    {
        _cfg = cfg;

        var rows = new StackPanel
        {
            Margin = new Thickness(24, 12, 24, 22),
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        rows.Children.Add(Section("MOVEMENT"));
        rows.Children.Add(NumberRow("Jump height", "px · highest platform it will hop to",
            cfg.JumpHeight, 0, 4000, 5, out _jump));
        rows.Children.Add(NumberRow("Walk speed", "px / second", cfg.WalkSpeed, 1, 2000, 5, out _walk));
        rows.Children.Add(NumberRow("Climb speed", "px / second", cfg.ClimbSpeed, 1, 2000, 5, out _climb));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("BEHAVIOR"));
        rows.Children.Add(NumberRow("Roaming level", "0–100 · how often it wanders when idle",
            cfg.RoamingLevel, 0, 100, 5, out _roam));
        rows.Children.Add(NumberRow("Gravity", "px / second²", cfg.Gravity, 1, 10000, 50, out _gravity));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("ADVANCED · TAKES EFFECT NEXT LAUNCH"));
        rows.Children.Add(NumberRow("Target FPS", "frames / second", cfg.TargetFps, 15, 240, 5, out _fps));
        rows.Children.Add(NumberRow("World poll", "Hz · how often window geometry is re-read",
            cfg.WorldPollHz, 1, 60, 1, out _poll));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("SYSTEM"));
        _startup = Toggle();
        _startup.IsChecked = StartupRegistration.IsEnabled();
        rows.Children.Add(ToggleRow("Start with Windows", "Launch MaplePet automatically when you sign in", _startup));
        _overlay = Toggle();
        _overlay.IsChecked = cfg.ShowOverlay;
        rows.Children.Add(ToggleRow("Debug overlay", "Draw window edges, platforms & the pet's path", _overlay));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = rows,
        };

        // Wire change handlers only now that initial values are in place, so populating the controls
        // doesn't trigger a (redundant) write.
        _ready = true;
        foreach (var n in new[] { _jump, _roam, _walk, _climb, _gravity, _fps, _poll })
            n.ValueChanged += (_, _) => ApplyLive();
        _overlay.IsCheckedChanged += (_, _) => ApplyLive();
        _startup.IsCheckedChanged += (_, _) => { if (_ready) ApplyStartup(); };
    }

    /// <summary>Push every control's value into the shared <see cref="Settings"/> and persist it.</summary>
    private void ApplyLive()
    {
        if (!_ready) return;
        static double D(NumericUpDown n, double fallback) => n.Value is { } v ? (double)v : fallback;

        _cfg.JumpHeight = D(_jump, _cfg.JumpHeight);
        _cfg.RoamingLevel = D(_roam, _cfg.RoamingLevel);
        _cfg.WalkSpeed = D(_walk, _cfg.WalkSpeed);
        _cfg.ClimbSpeed = D(_climb, _cfg.ClimbSpeed);
        _cfg.Gravity = D(_gravity, _cfg.Gravity);
        _cfg.TargetFps = (int)D(_fps, _cfg.TargetFps);
        _cfg.WorldPollHz = D(_poll, _cfg.WorldPollHz);
        _cfg.ShowOverlay = _overlay.IsChecked ?? _cfg.ShowOverlay;
        _cfg.Save();
    }

    /// <summary>Apply the "Start with Windows" toggle to the registry, then re-read so the switch
    /// reflects the actual state if the write was blocked.</summary>
    private void ApplyStartup()
    {
        StartupRegistration.SetEnabled(_startup.IsChecked ?? false);
        bool prev = _ready;
        _ready = false;
        _startup.IsChecked = StartupRegistration.IsEnabled();
        _ready = prev;
    }

    // ------------------------------------------------------------------ row builders
    private static ToggleSwitch Toggle() => new()
    {
        OnContent = "",
        OffContent = "",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("sectionHeader");
        return t;
    }

    private static Border Divider()
    {
        var b = new Border();
        b.Classes.Add("hairline");
        return b;
    }

    private static Control NumberRow(string label, string caption, double value,
        double min, double max, double step, out NumericUpDown box)
    {
        box = new NumericUpDown
        {
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            Value = (decimal)value,
            Width = 124,
            FormatString = "0.##",
            VerticalAlignment = VerticalAlignment.Center,
        };
        return Row(label, caption, box);
    }

    private static Control ToggleRow(string label, string caption, ToggleSwitch toggle) =>
        Row(label, caption, toggle);

    private static Control Row(string label, string caption, Control control)
    {
        var labelBlock = new TextBlock { Text = label };
        labelBlock.Classes.Add("rowLabel");
        var captionBlock = new TextBlock { Text = caption, Margin = new Thickness(0, 1, 0, 0) };
        captionBlock.Classes.Add("caption");

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { labelBlock, captionBlock },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }
}
