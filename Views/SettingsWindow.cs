using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MaplePet.Engine;

namespace MaplePet.Views;

/// <summary>
/// A small dialog (opened from the tray menu) to edit the configurable parameters. Physics
/// values apply live (the controller reads <see cref="Settings"/> each tick); FPS / poll rate
/// take effect on the next launch. Changes are persisted to settings.json on Save.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly Settings _cfg;
    private readonly NumericUpDown _jump, _roam, _walk, _climb, _gravity, _fps, _poll;
    private readonly CheckBox _overlay;

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
        rows.Children.Add(Row("Target FPS", cfg.TargetFps, 15, 240, 5, out _fps));
        rows.Children.Add(Row("World poll (Hz)", cfg.WorldPollHz, 1, 60, 1, out _poll));

        _overlay = new CheckBox { Content = "Show window/path overlay", IsChecked = cfg.ShowOverlay };
        rows.Children.Add(_overlay);

        var save = new Button { Content = "Save", Width = 80, IsDefault = true };
        save.Click += (_, _) => { Apply(); Close(); };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        rows.Children.Add(buttons);

        Content = new Border { Padding = new Thickness(16), Child = rows };
    }

    private void Apply()
    {
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
