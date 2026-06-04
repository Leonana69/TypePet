using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    // (true)=suspend the live global hotkey while a capture is in progress; (false)=re-arm it afterwards.
    private readonly Action<bool>? _onHotkeyCapture;
    private readonly NumericUpDown _jump, _roam, _walk, _climb, _gravity, _fps, _poll, _mcpPort;
    private readonly ToggleSwitch _overlay, _startup, _mcp, _hideFullscreen;
    private readonly Button _hotkeyBtn;
    private string _hotkeyText = "";    // the persisted gesture, mirrored into _cfg by ApplyLive
    private string _hotkeyBefore = "";  // button text to restore if a capture is cancelled
    private bool _capturing;            // a key-capture is in progress
    private bool _ready; // suppress change handlers while the initial values are being set

    public SettingsView(Settings cfg, Action<bool>? onHotkeyCapture = null)
    {
        _cfg = cfg;
        _onHotkeyCapture = onHotkeyCapture;

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
        rows.Children.Add(Section("SAY INPUT"));
        // Show a hand-edited/invalid persisted gesture as unset rather than as a fake-active binding.
        _hotkeyText = HotkeyGesture.IsBindable(cfg.SayInputHotkey) ? cfg.SayInputHotkey : "";
        _hotkeyBtn = HotkeyButton(_hotkeyText);
        rows.Children.Add(Row("Open hotkey",
            "Global shortcut to pop up the say box · you can also double-click the pet", _hotkeyBtn));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("ADVANCED · TAKES EFFECT NEXT LAUNCH"));
        rows.Children.Add(NumberRow("Target FPS", "frames / second", cfg.TargetFps, 15, 240, 5, out _fps));
        rows.Children.Add(NumberRow("World poll", "Hz · how often window geometry is re-read",
            cfg.WorldPollHz, 1, 60, 1, out _poll));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("CONTROL API · TAKES EFFECT NEXT LAUNCH"));
        _mcp = Toggle();
        _mcp.IsChecked = cfg.EnableMcpServer;
        rows.Children.Add(ToggleRow("LLM control server (MCP)",
            "Expose the pet on a local MCP server so an LLM can drive it", _mcp));
        rows.Children.Add(NumberRow("Server port", "localhost port the MCP server listens on",
            cfg.McpPort, 1, 65535, 1, out _mcpPort));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("SYSTEM"));
        _startup = Toggle();
        _startup.IsChecked = StartupRegistration.IsEnabled();
        rows.Children.Add(ToggleRow("Start with Windows", "Launch MaplePet automatically when you sign in", _startup));
        _hideFullscreen = Toggle();
        _hideFullscreen.IsChecked = cfg.HideWhenFullscreen;
        rows.Children.Add(ToggleRow("Hide in fullscreen apps",
            "Tuck the pet away while a borderless or fullscreen game/video is in front", _hideFullscreen));
        _overlay = Toggle();
        _overlay.IsChecked = cfg.ShowOverlay;
        rows.Children.Add(ToggleRow("Debug overlay", "Draw window edges, platforms & the pet's path", _overlay));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("ABOUT"));
        rows.Children.Add(Row(AppInfo.Name, "A desktop pet that walks along your taskbar", VersionValue()));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = rows,
        };

        // Wire change handlers only now that initial values are in place, so populating the controls
        // doesn't trigger a (redundant) write.
        _ready = true;
        foreach (var n in new[] { _jump, _roam, _walk, _climb, _gravity, _fps, _poll, _mcpPort })
            n.ValueChanged += (_, _) => ApplyLive();
        _overlay.IsCheckedChanged += (_, _) => ApplyLive();
        _hideFullscreen.IsCheckedChanged += (_, _) => ApplyLive();
        _mcp.IsCheckedChanged += (_, _) => { _mcpPort.IsEnabled = _mcp.IsChecked == true; ApplyLive(); };
        _mcpPort.IsEnabled = _mcp.IsChecked == true; // the port only matters when the server is on
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
        _cfg.HideWhenFullscreen = _hideFullscreen.IsChecked ?? _cfg.HideWhenFullscreen;
        _cfg.EnableMcpServer = _mcp.IsChecked ?? _cfg.EnableMcpServer;
        _cfg.McpPort = (int)D(_mcpPort, _cfg.McpPort);
        _cfg.SayInputHotkey = _hotkeyText;
        _cfg.Save();
    }

    // ------------------------------------------------------------------ hotkey capture
    private Button HotkeyButton(string gesture)
    {
        var b = new Button
        {
            Content = string.IsNullOrEmpty(gesture) ? "Click to set" : gesture,
            Width = 124,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("ghost");
        b.Click += (_, _) => BeginCapture();
        // Intercept keys (tunnel = preview) while capturing, before the focused button turns
        // Space/Enter into a click; handledEventsToo so we still see keys others marked handled.
        b.AddHandler(InputElement.KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        b.LostFocus += (_, _) => { if (_capturing) CancelCapture(); };
        // Detaching (tab switch / window close) mid-capture must also re-arm the suspended hotkey.
        b.DetachedFromVisualTree += (_, _) => { if (_capturing) CancelCapture(); };
        return b;
    }

    private void BeginCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _onHotkeyCapture?.Invoke(true); // suspend the live hotkey so the chord reaches this capture, not the hook
        _hotkeyBefore = _hotkeyBtn.Content as string ?? "";
        _hotkeyBtn.Content = "Press keys…";
    }

    private void CancelCapture()
    {
        _capturing = false;
        _hotkeyBtn.Content = _hotkeyBefore;
        _onHotkeyCapture?.Invoke(false); // re-arm the (unchanged) hotkey
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true; // swallow everything while capturing so focus/clicks can't escape

        if (e.Key == Key.Escape) { CancelCapture(); return; }
        if (HotkeyGesture.IsModifierKey(e.Key)) return; // wait for a non-modifier key

        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
        // Require a strong modifier + a mappable key, so we never bind a bare key (which would swallow
        // that key everywhere) as a global hotkey.
        bool strong = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta);
        if (!strong || HotkeyGesture.KeyToVirtualKey(e.Key) is null) return; // keep listening

        _hotkeyText = HotkeyGesture.Format(mods, e.Key);
        _hotkeyBtn.Content = _hotkeyText;
        _capturing = false;
        ApplyLive();                     // persists _hotkeyText into settings.json
        _onHotkeyCapture?.Invoke(false); // re-arm the live hotkey with the new chord
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

    /// <summary>The right-hand "v1.0.0" value for the About row (version comes from <see cref="AppInfo"/>).</summary>
    private static Control VersionValue()
    {
        var t = new TextBlock { Text = $"v{AppInfo.Version}", VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("rowLabel");
        return t;
    }

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
