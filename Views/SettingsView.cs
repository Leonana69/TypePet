using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MaplePet.Api.Chat;
using MaplePet.Engine;
using MaplePet.Platform;

namespace MaplePet.Views;

/// <summary>
/// The Settings tab of <see cref="ConfigWindow"/>: the configurable parameters grouped into Movement /
/// Behavior / Advanced / System sections. Every change applies immediately — the controller reads
/// <see cref="Settings"/> each tick — and is persisted to settings.json on the spot, so there is no
/// Save button. Target FPS / poll rate only take effect on the next launch. "Start at login" is
/// backed by the OS (registry on Windows, SMAppService on macOS) via
/// <see cref="PlatformServices.StartupAtLogin"/>, not settings.json.
/// </summary>
public sealed class SettingsView : UserControl
{
    private const double FieldHeight = 34; // shared height so the provider/model/key fields line up

    private readonly Settings _cfg;
    // (true)=suspend the live global hotkey while a capture is in progress; (false)=re-arm it afterwards.
    private readonly Action<bool>? _onHotkeyCapture;
    private readonly NumericUpDown _jump, _roam, _walk, _climb, _gravity, _fps, _poll, _mcpPort;
    private readonly ToggleSwitch _overlay, _startup, _mcp, _hideFullscreen, _chatbot, _webSearch;
    private readonly ComboBox _provider;
    private readonly Panel _providerHost;
    private readonly Button _hotkeyBtn;
    private string _hotkeyText = "";    // the persisted gesture, mirrored into _cfg by ApplyLive
    private string _hotkeyBefore = "";  // button text to restore if a capture is cancelled
    private bool _capturing;            // a key-capture is in progress
    private bool _ready; // suppress change handlers while the initial values are being set
    private bool _clamping;             // reentrancy guard while a range-clamp rewrites a value
    private Flyout? _rangeFlyout;       // the transient "out of range" message bubble (reused)
    private IDisposable? _rangeHide;    // pending auto-hide for _rangeFlyout

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
        rows.Children.Add(RangeRow("Jump height", "px · highest platform it will hop to",
            cfg.JumpHeight, 100, 300, 5, out _jump));
        rows.Children.Add(RangeRow("Walk speed", "px / second", cfg.WalkSpeed, 20, 900, 5, out _walk));
        rows.Children.Add(RangeRow("Climb speed", "px / second", cfg.ClimbSpeed, 20, 900, 5, out _climb));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("BEHAVIOR"));
        rows.Children.Add(RangeRow("Roaming level", "0–100 · how often it wanders when idle",
            cfg.RoamingLevel, 0, 100, 5, out _roam));
        rows.Children.Add(RangeRow("Gravity", "px / second²", cfg.Gravity, 20, 10000, 50, out _gravity));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("ADVANCED · TAKES EFFECT NEXT LAUNCH"));
        rows.Children.Add(RangeRow("Target FPS", "frames / second", cfg.TargetFps, 30, 120, 5, out _fps));
        rows.Children.Add(RangeRow("World poll", "Hz · how often window geometry is re-read",
            cfg.WorldPollHz, 2, 16, 1, out _poll));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("CONTROL API · TAKES EFFECT NEXT LAUNCH"));
        _mcp = Toggle();
        _mcp.IsChecked = cfg.EnableMcpServer;
        rows.Children.Add(ToggleRow("LLM control server (MCP)",
            "Expose the pet on a local MCP server so an LLM can drive it", _mcp));
        rows.Children.Add(NumberRow("Server port", "localhost port the MCP server listens on",
            cfg.McpPort, 1, 65535, 1, out _mcpPort));

        rows.Children.Add(Divider());
        rows.Children.Add(Section("CHATBOT · TAKES EFFECT NEXT LAUNCH"));
        _chatbot = Toggle();
        _chatbot.IsChecked = cfg.EnableChatbot;
        rows.Children.Add(ToggleRow("Enable chatbot",
            "Type in the input bar to chat with an LLM — it can search the web and the pet reacts and speaks the reply. Off = the pet just says what you type.", _chatbot));
        _webSearch = Toggle();
        _webSearch.IsChecked = cfg.EnableWebSearch;
        rows.Children.Add(ToggleRow("Web search",
            "Let the chatbot search the web and read pages — keyless, via DuckDuckGo. No search key needed.", _webSearch));

        // The input bar's open shortcut (also double-click the pet). Lives here since the bar is the chat.
        _hotkeyText = HotkeyGesture.IsBindable(cfg.SayInputHotkey) ? cfg.SayInputHotkey : "";
        _hotkeyBtn = HotkeyButton(_hotkeyText);
        rows.Children.Add(Row("Open hotkey",
            "Global shortcut to open the input bar · you can also double-click the pet", _hotkeyBtn));

        // Pick the active provider; only its fields show below. Keys go to the encrypted secret store
        // (never settings.json); model/base-URL edits persist to the profile. String items (not the
        // ProviderProfile objects) so the closed combo reliably shows the selected name.
        _provider = new ComboBox
        {
            ItemsSource = cfg.Providers.Select(p => p.DisplayName).ToList(),
            SelectedIndex = Math.Max(0, cfg.Providers.FindIndex(p => p.Id == cfg.ActiveProviderId)),
            Width = 240,
            Height = FieldHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rows.Children.Add(Row("Provider", "Which LLM the chat uses", _provider, topAlign: true));
        // No extra margin here: each Row already carries Thickness(0, 6), so the host must add nothing
        // or the Provider→API-key gap would exceed the uniform 12px spacing of the rows below it.
        _providerHost = new StackPanel();
        rows.Children.Add(_providerHost);
        RebuildProviderPanel();

        rows.Children.Add(Divider());
        rows.Children.Add(Section("SYSTEM"));
        _startup = Toggle();
        _startup.IsChecked = PlatformServices.StartupAtLogin.IsEnabled();
        _startup.IsEnabled = PlatformServices.StartupAtLogin.IsSupported; // disabled on platforms that can't register (e.g. unbundled macOS)
        rows.Children.Add(ToggleRow("Start at login", "Launch MaplePet automatically when you sign in", _startup));
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
        _chatbot.IsCheckedChanged += (_, _) =>
        {
            if (!_ready) return;
            _cfg.EnableChatbot = _chatbot.IsChecked ?? false;
            _cfg.Save();
        };
        _webSearch.IsCheckedChanged += (_, _) =>
        {
            if (!_ready) return;
            _cfg.EnableWebSearch = _webSearch.IsChecked ?? false;
            _cfg.Save();
        };
        _provider.SelectionChanged += (_, _) =>
        {
            int i = _provider.SelectedIndex;
            if (i >= 0 && i < _cfg.Providers.Count) { _cfg.ActiveProviderId = _cfg.Providers[i].Id; _cfg.Save(); }
            RebuildProviderPanel();
        };
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

    /// <summary>Apply the "Start at login" toggle to the OS, then re-read so the switch reflects the
    /// actual state if the write was blocked.</summary>
    private void ApplyStartup()
    {
        PlatformServices.StartupAtLogin.SetEnabled(_startup.IsChecked ?? false);
        bool prev = _ready;
        _ready = false;
        _startup.IsChecked = PlatformServices.StartupAtLogin.IsEnabled();
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

    /// <summary>A numeric row whose value is capped to <paramref name="min"/>–<paramref name="max"/>. The
    /// soft range is enforced in code (and deliberately NOT shown in the caption): an out-of-range entry is
    /// clamped and a small, self-dismissing flyout reports the limit. The <see cref="NumericUpDown"/> keeps
    /// generous hard bounds so a typed value reaches the handler instead of being silently coerced first
    /// (which would also suppress the change event when the field is already sitting at the cap).</summary>
    private Control RangeRow(string label, string caption, double value,
        double min, double max, double step, out NumericUpDown box)
    {
        var b = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1_000_000,
            Increment = (decimal)step,
            Value = (decimal)Math.Clamp(value, min, max),
            Width = 124,
            FormatString = "0.##",
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Attached before ApplyLive (wired later in the ctor) so the value is capped before it's persisted.
        b.ValueChanged += (_, _) => ClampToRange(b, label, min, max);
        box = b;
        return Row(label, caption, b);
    }

    /// <summary>Cap <paramref name="box"/> to [<paramref name="min"/>, <paramref name="max"/>] and, when a
    /// value was actually out of range, show a brief flyout. Guarded against the re-entrant change raised by
    /// writing the clamped value back.</summary>
    private void ClampToRange(NumericUpDown box, string label, double min, double max)
    {
        if (!_ready || _clamping || box.Value is not { } dv) return;
        double v = (double)dv;
        double clamped = Math.Clamp(v, min, max);
        if (clamped == v) return;

        _clamping = true;
        box.Value = (decimal)clamped;
        _clamping = false;
        ShowRangeMessage(box, $"{label} must be {min:0.##}–{max:0.##}");
    }

    /// <summary>Pop a small, self-dismissing flyout by <paramref name="target"/> — the only place a field's
    /// valid range is surfaced (the row captions intentionally don't list it).</summary>
    private void ShowRangeMessage(Control target, string message)
    {
        _rangeHide?.Dispose();
        _rangeFlyout?.Hide();
        var f = new Flyout
        {
            Placement = PlacementMode.Top,
            ShowMode = FlyoutShowMode.Transient, // shown without stealing focus from the field
            Content = new TextBlock { Text = message, MaxWidth = 260, TextWrapping = TextWrapping.Wrap },
        };
        _rangeFlyout = f;
        f.ShowAt(target);
        _rangeHide = DispatcherTimer.RunOnce(() => f.Hide(), TimeSpan.FromSeconds(2.2));
    }

    private static Control ToggleRow(string label, string caption, ToggleSwitch toggle) =>
        Row(label, caption, toggle);

    /// <summary>Rebuild the per-provider field panel for the currently selected provider.</summary>
    private void RebuildProviderPanel()
    {
        int i = _provider.SelectedIndex;
        var p = (i >= 0 && i < _cfg.Providers.Count) ? _cfg.Providers[i] : _cfg.Providers.FirstOrDefault();
        _providerHost.Children.Clear();
        if (p is not null) _providerHost.Children.Add(BuildProviderPanel(p));
    }

    /// <summary>The fields for one provider: API key (masked), model (a picklist auto-populated from the
    /// provider, free-text otherwise), and base URL (OpenAI-compatible providers only).</summary>
    private Control BuildProviderPanel(ProviderProfile p)
    {
        var secrets = PlatformServices.SecretStore;
        var panel = new StackPanel();

        panel.Children.Add(TextRow("API key",
            p.Kind == "anthropic" ? "Anthropic key (sk-ant-…)" : "Provider key (blank for keyless local servers)",
            secrets.Get(p.Id) ?? "", 240, v => secrets.Set(p.Id, v), passwordChar: '•', topAlign: true));

        // Model picklist: an AutoCompleteBox that shows the full fetched list on focus (MinimumPrefixLength
        // = 0) and still allows typing a custom id if the list is empty/unavailable.
        var model = new AutoCompleteBox
        {
            Text = p.Model,
            Width = 240,
            Height = FieldHeight,
            Watermark = "model id",
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
        };
        model.TextChanged += (_, _) => { p.Model = model.Text ?? ""; _cfg.Save(); };
        panel.Children.Add(Row("Model", "Pick or type the model id", model, topAlign: true));

        if (p.Kind != "anthropic")
            panel.Children.Add(TextRow("Base URL", "OpenAI-compatible endpoint (blank = provider default)",
                p.BaseUrl, 240, v => { p.BaseUrl = v; _cfg.Save(); }, topAlign: true));

        // Populate the picklist from the provider (best-effort; stays free-text on failure).
        if (!p.UsesKey || !string.IsNullOrEmpty(secrets.Get(p.Id)))
            _ = LoadModelsAsync(p, model);

        return panel;
    }

    /// <summary>Fetch the provider's model ids into the picklist. Best-effort: on failure the box stays a
    /// free-text field.</summary>
    private static async Task LoadModelsAsync(ProviderProfile p, AutoCompleteBox box)
    {
        var secrets = PlatformServices.SecretStore;
        string key = p.UsesKey ? (secrets.Get(p.Id) ?? "") : "";
        try
        {
            var backend = ChatBackendFactory.Create(p.Kind, p.BaseUrl, key,
                string.IsNullOrEmpty(p.Model) ? "model" : p.Model);
            var models = await backend.ListModelsAsync(CancellationToken.None);
            if (models.Count > 0) box.ItemsSource = models;
        }
        catch { /* leave as free text */ }
    }

    /// <summary>A text-input row. <paramref name="onChanged"/> fires on edits only (the initial value is
    /// set before the handler is attached). Pass <paramref name="passwordChar"/> to mask a secret.</summary>
    private static Control TextRow(string label, string caption, string initial, double width,
        Action<string> onChanged, char? passwordChar = null, bool topAlign = false)
    {
        var box = new TextBox
        {
            Text = initial,
            Width = width,
            Height = FieldHeight,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (passwordChar is char pc) box.PasswordChar = pc;
        box.TextChanged += (_, _) => onChanged(box.Text ?? "");
        return Row(label, caption, box, topAlign);
    }

    /// <summary>The right-hand "v1.0.0" value for the About row (version comes from <see cref="AppInfo"/>).</summary>
    private static Control VersionValue()
    {
        var t = new TextBlock { Text = $"v{AppInfo.Version}", VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("rowLabel");
        return t;
    }

    // topAlign anchors the label/caption and the control to the row's top instead of centering them.
    // Use it for rows whose caption can wrap to a 2nd line (chatbot fields): centering would pad the
    // extra height ABOVE the control, so a single tall row would widen the gap to the row before it.
    // Top-anchoring keeps every box on the same rhythm and lets the wrapped caption hang below.
    private static Control Row(string label, string caption, Control control, bool topAlign = false)
    {
        var labelBlock = new TextBlock { Text = label };
        labelBlock.Classes.Add("rowLabel");
        var captionBlock = new TextBlock { Text = caption, Margin = new Thickness(0, 1, 0, 0) };
        captionBlock.Classes.Add("caption");

        var align = topAlign ? VerticalAlignment.Top : VerticalAlignment.Center;
        var text = new StackPanel
        {
            VerticalAlignment = align,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { labelBlock, captionBlock },
        };
        if (topAlign) control.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }
}
