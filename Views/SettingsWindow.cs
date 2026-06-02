using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MaplePet.Engine;

namespace MaplePet.Views;

/// <summary>
/// A small dialog (opened from the tray menu) to edit the configurable parameters. Every change
/// applies immediately — the controller reads <see cref="Settings"/> each tick — and is persisted to
/// settings.json on the spot, so there is no Save button; just close the window. Target FPS / poll
/// rate only take effect on the next launch.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly Settings _cfg;
    private readonly NumericUpDown _jump, _roam, _walk, _climb, _gravity, _fps, _poll;
    private readonly CheckBox _overlay;
    private bool _ready; // suppress change handlers while the initial values are being set

    public SettingsWindow(Settings cfg)
    {
        _cfg = cfg;
        Title = "MaplePet Settings";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(new TextBlock { Text = "MaplePet Settings", FontWeight = FontWeight.Bold, FontSize = 16 });
        rows.Children.Add(Row("Jump height (px)", cfg.JumpHeight, 0, 4000, 5, out _jump));
        rows.Children.Add(Row("Roaming level (0-100)", cfg.RoamingLevel, 0, 100, 5, out _roam));
        rows.Children.Add(Row("Walk speed (px/s)", cfg.WalkSpeed, 1, 2000, 5, out _walk));
        rows.Children.Add(Row("Climb speed (px/s)", cfg.ClimbSpeed, 1, 2000, 5, out _climb));
        rows.Children.Add(Row("Gravity (px/s^2)", cfg.Gravity, 1, 10000, 50, out _gravity));
        rows.Children.Add(Row("Target FPS (next launch)", cfg.TargetFps, 15, 240, 5, out _fps));
        rows.Children.Add(Row("World poll Hz (next launch)", cfg.WorldPollHz, 1, 60, 1, out _poll));

        _overlay = new CheckBox { Content = "Show window/path overlay", IsChecked = cfg.ShowOverlay };
        rows.Children.Add(_overlay);

        rows.Children.Add(new TextBlock { Text = "Changes apply immediately.", FontSize = 11, Opacity = 0.7 });

        var close = new Button
        {
            Content = "Close",
            Width = 80,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        rows.Children.Add(close);

        Content = new Border { Padding = new Thickness(16), Child = rows };

        // Wire change handlers only now that initial values are in place, so populating the controls
        // doesn't trigger a (redundant) write.
        _ready = true;
        foreach (var n in new[] { _jump, _roam, _walk, _climb, _gravity, _fps, _poll })
            n.ValueChanged += (_, _) => ApplyLive();
        _overlay.IsCheckedChanged += (_, _) => ApplyLive();
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

    private static Control Row(string label, double value, double min, double max, double step, out NumericUpDown box)
    {
        box = new NumericUpDown
        {
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            Value = (decimal)value,
            Width = 130,
            FormatString = "0.##",
        };
        var dock = new DockPanel();
        DockPanel.SetDock(box, Dock.Right);
        dock.Children.Add(box);
        dock.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return dock;
    }
}
