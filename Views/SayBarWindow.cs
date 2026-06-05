using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MaplePet.Api;
using MaplePet.Api.Chat;
using MaplePet.Engine;

namespace MaplePet.Views;

/// <summary>
/// The single input bar — a macOS-Spotlight-style frosted bar near the bottom-center, opened by
/// double-clicking the pet or the global hotkey. It is BOTH the "say" box and the chatbot input:
/// <list type="bullet">
/// <item>Chatbot off (or no provider configured): type → the pet says it → the bar dismisses.</item>
/// <item>Chatbot on, history FOLDED: type → the bar dismisses → the pet "thinks" then speaks the reply.</item>
/// <item>Chatbot on, history OPEN: the conversation shows above the input and the bar STAYS open after
/// each message (Esc / click-away dismiss; in open mode click-away keeps it).</item>
/// </list>
/// A persistent singleton (the app hides rather than destroys it via <see cref="HideRequested"/>) so the
/// conversation survives between opens. While awaiting a reply the pet's speech bubble shows a small
/// thinking animation; the full reply is then spoken (duration scales with length).
/// </summary>
public sealed class SayBarWindow : Window
{
    private const double BarWidth = 600;
    private const double BottomMarginLogical = 120;
    private const double HistoryMaxHeight = 320;

    private static readonly string[] ThinkFrames = { "•", "• •", "• • •" };

    private readonly Settings _cfg;
    private readonly Func<PetChatAgent?> _agent;
    private readonly Func<IPetControl?> _control;
    private readonly Func<bool> _chatConfigured;
    private readonly ChatCommands _commands;

    private readonly TextBox _input;
    private readonly StackPanel _history;
    private readonly ScrollViewer _historyScroller;
    private readonly Border _historyHost;
    private readonly Button _toggle;

    private bool _everActivated;
    private bool _busy;
    private DispatcherTimer? _thinkTimer;
    private int _thinkFrame;

    /// <summary>Raised when the bar should be dismissed (Esc, click-away, or after a folded-mode send).
    /// The app hides the window (keeping it alive) and clears the overlay's topmost suppression.</summary>
    public event Action? HideRequested;

    public SayBarWindow(Settings cfg, Func<PetChatAgent?> agent, Func<IPetControl?> control,
        Func<bool> chatConfigured, ChatCommands commands)
    {
        _cfg = cfg;
        _agent = agent;
        _control = control;
        _chatConfigured = chatConfigured;
        _commands = commands;

        // Frosted-glass plumbing (borderless acrylic, DWM round/shadow on Win11).
        Title = "MaplePet";
        SystemDecorations = SystemDecorations.Full;
        CanResize = false;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = new SolidColorBrush(FrostTheme.SurfaceFallback);
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent,
        };
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Width = BarWidth;
        SizeToContent = SizeToContent.Height; // grows/shrinks as history is shown/hidden

        // --- history (collapsible, above the input) ---
        _history = new StackPanel { Spacing = 8, Margin = new Thickness(18, 14, 18, 4) };
        _historyScroller = new ScrollViewer
        {
            Content = _history,
            MaxHeight = HistoryMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _historyHost = new Border { Child = _historyScroller, IsVisible = false };

        // --- input row ---
        _input = new TextBox
        {
            Watermark = "Say something…",
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            MaxLength = 2000,
        };
        _input.Classes.Add("sayInput");
        _input.KeyDown += OnInputKeyDown;

        _toggle = new Button { VerticalAlignment = VerticalAlignment.Center, IsVisible = cfg.EnableChatbot };
        _toggle.Classes.Add("ghost");
        ToolTip.SetTip(_toggle, "Show/hide chat history");
        _toggle.Click += (_, _) => SetHistoryOpen(!_cfg.ChatHistoryVisible);
        UpdateToggleGlyph();

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(18, 0, 12, 0),
            Height = 60,
        };
        if (AppIcon.Bitmap is { } iconBmp)
        {
            var img = new Image
            {
                Width = 22,
                Height = 22,
                Source = iconBmp,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
            Grid.SetColumn(img, 0);
            row.Children.Add(img);
        }
        Grid.SetColumn(_input, 1);
        Grid.SetColumn(_toggle, 2);
        row.Children.Add(_input);
        row.Children.Add(_toggle);

        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Bottom);
        dock.Children.Add(row);
        dock.Children.Add(_historyHost);

        var acrylic = new ExperimentalAcrylicBorder
        {
            IsHitTestVisible = false,
            Material = new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = FrostTheme.SurfaceTint,
                TintOpacity = 1.0,
                MaterialOpacity = 0.82,
                FallbackColor = FrostTheme.SurfaceFallback,
            },
        };

        Content = new Panel { Children = { acrylic, dock } };

        SizeChanged += (_, _) => PositionAtBottomCenter();
        Activated += (_, _) => _everActivated = true;
        Deactivated += (_, _) => { if (_everActivated && !KeepVisible) HideRequested?.Invoke(); };
    }

    /// <summary>True while the bar should persist through click-away — i.e. chat mode with history open.</summary>
    private bool KeepVisible => _cfg.EnableChatbot && _cfg.ChatHistoryVisible;

    public void FocusInput() => _input.Focus();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _toggle.IsVisible = _cfg.EnableChatbot; // reflect a settings change since last open
        PositionAtBottomCenter();
        _input.Focus();
        Dispatcher.UIThread.Post(() => _everActivated = true);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideRequested?.Invoke(); e.Handled = true; }
    }

    // async void — MUST NOT let any exception escape, or it crashes the process. The whole body is guarded.
    private async void Submit()
    {
        if (_busy) return;
        var text = _input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _input.Text = "";

        IPetControl? control = null;
        bool keepOpen = false;
        _busy = true;
        try
        {
            control = _control();

            // Slash command (e.g. "/rank Name") — handled locally and deterministically, with NO LLM, so
            // it works even when the chatbot is off. Shown the same way as a reply: history bubble when
            // the panel is open, otherwise the bar dismisses and the pet speaks the result.
            if (ChatCommands.IsCommand(text))
            {
                keepOpen = _cfg.EnableChatbot && _cfg.ChatHistoryVisible;
                if (keepOpen) AddUserBubble(text);
                else HideRequested?.Invoke();

                if (control is not null) StartThinking(control);
                var cmd = await _commands.RunAsync(text, CancellationToken.None);
                StopThinking();
                var ctext = string.IsNullOrWhiteSpace(cmd.Text) ? "…" : cmd.Text;
                // A command link (e.g. /rank's MapleRanks page) shows as a clickable line in the pet's bubble
                // (and, when open, in history). A bubble link gets a longer dwell so there's time to click it.
                // The bubble link is ALWAYS shown; it's only made click-hittable while no focusable window is
                // up (see PetWindow.TickInput), so it can't swallow presses meant for an open say bar.
                double secs = cmd.Link is null ? ChatSpeechSeconds(ctext) : Math.Max(ChatSpeechSeconds(ctext), 12);
                _ = control?.Say(ctext, secs, cmd.Link?.Url, cmd.Link?.Title, cmd.ImageUrl);
                if (keepOpen) { AddAssistantBubble(ctext, cmd.Sources, cmd.IsError, cmd.Link, cmd.ImageUrl); _input.Focus(); }
                return;
            }

            var agent = _agent();
            bool chat = _cfg.EnableChatbot && agent is not null && control is not null && SafeChatConfigured();

            if (!chat)
            {
                control?.Say(text);     // plain "say" — the pet repeats the text
                HideRequested?.Invoke();
                return;
            }

            keepOpen = _cfg.ChatHistoryVisible;
            if (keepOpen) AddUserBubble(text);
            else HideRequested?.Invoke(); // folded: dismiss now; the pet thinks + speaks the reply

            StartThinking(control!);
            var result = await agent!.SendAsync(text, CancellationToken.None);
            StopThinking();
            var reply = string.IsNullOrWhiteSpace(result.Text) ? "…" : result.Text;
            _ = control!.Say(reply, ChatSpeechSeconds(reply));
            if (keepOpen) { AddAssistantBubble(reply, result.Sources, result.IsError); _input.Focus(); }
        }
        catch (Exception ex)
        {
            StopThinking();
            try { _ = control?.Say($"(chat error: {ex.Message})", 5); } catch { /* ignore */ }
            if (keepOpen) { try { AddAssistantBubble($"Error: {ex.Message}", Array.Empty<WebSource>(), true); } catch { /* ignore */ } }
        }
        finally { _busy = false; }
    }

    private bool SafeChatConfigured()
    {
        try { return _chatConfigured(); }
        catch { return false; }
    }

    // ---- pet "thinking" animation ------------------------------------------------

    private void StartThinking(IPetControl control)
    {
        _thinkFrame = 0;
        _ = control.Say(ThinkFrames[0], 2);
        _thinkTimer?.Stop();
        _thinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _thinkTimer.Tick += (_, _) =>
        {
            // Runs on the UI-thread dispatcher; an escaping exception here would crash the app.
            try
            {
                _thinkFrame = (_thinkFrame + 1) % ThinkFrames.Length;
                _ = control.Say(ThinkFrames[_thinkFrame], 2);
            }
            catch { /* ignore */ }
        };
        _thinkTimer.Start();
    }

    private void StopThinking()
    {
        _thinkTimer?.Stop();
        _thinkTimer = null;
    }

    private static double ChatSpeechSeconds(string text) => Math.Clamp(3 + text.Length * 0.07, 3, 30);

    // ---- history fold ------------------------------------------------------------

    private void SetHistoryOpen(bool open)
    {
        _cfg.ChatHistoryVisible = open;
        _cfg.Save();
        UpdateHistoryVisibility();
        UpdateToggleGlyph();
    }

    private void UpdateHistoryVisibility() =>
        _historyHost.IsVisible = _cfg.ChatHistoryVisible && _history.Children.Count > 0;

    private void UpdateToggleGlyph() => _toggle.Content = _cfg.ChatHistoryVisible ? "⌄" : "⌃";

    // ---- message bubbles ---------------------------------------------------------

    private void AddUserBubble(string text)
    {
        _history.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0x20, 0xC9, 0xC2)),
            CornerRadius = new CornerRadius(12, 12, 2, 12),
            Padding = new Thickness(11, 8),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = FrostTheme.TextPrimary },
        });
        UpdateHistoryVisibility();
        ScrollToEnd();
    }

    private void AddAssistantBubble(string text, IReadOnlyList<WebSource> sources, bool isError,
        WebSource? link = null, string? imageUrl = null)
    {
        var body = new StackPanel();

        // Optional character image (e.g. /rank's canvas), shown atop the card and loaded async (cached).
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, // match the bubble: clamp big images, never upscale
                MaxWidth = 120,
                MaxHeight = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
            };
            body.Children.Add(img);
            LoadImageInto(img, imageUrl!);
        }

        body.Children.Add(new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isError ? FrostTheme.StatusError : FrostTheme.TextPrimary,
        });

        // A primary "more info" link (e.g. /rank's MapleRanks page). It's an actionable destination, not a
        // citation, so it's shown on its own — NOT under the "Sources" heading.
        if (link is not null)
        {
            var more = new Button { Content = Trim(link.Title, link.Url), Margin = new Thickness(0, 6, 0, 0) };
            more.Classes.Add("link");
            more.HorizontalAlignment = HorizontalAlignment.Left;
            more.HorizontalContentAlignment = HorizontalAlignment.Left;
            var lurl = link.Url;
            more.Click += (_, _) => OpenUrl(lurl);
            body.Children.Add(more);
        }

        if (sources.Count > 0)
        {
            var src = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            src.Children.Add(new TextBlock { Text = "Sources", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = FrostTheme.TextSecondary });
            foreach (var s in sources)
            {
                var srcLink = new Button { Content = Trim(s.Title, s.Url) };
                srcLink.Classes.Add("link");
                srcLink.HorizontalAlignment = HorizontalAlignment.Left;
                srcLink.HorizontalContentAlignment = HorizontalAlignment.Left;
                var url = s.Url;
                srcLink.Click += (_, _) => OpenUrl(url);
                src.Children.Add(srcLink);
            }
            body.Children.Add(src);
        }

        _history.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(12, 12, 12, 2),
            Padding = new Thickness(11, 8),
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = body,
        });
        UpdateHistoryVisibility();
        ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => _historyScroller.Offset = new Vector(0, double.MaxValue), DispatcherPriority.Background);

    /// <summary>Load <paramref name="url"/> (cached) and set it as the image's source once ready. Fire and
    /// forget; failures leave the placeholder blank.</summary>
    private static async void LoadImageInto(Image target, string url)
    {
        try
        {
            var bmp = await MaplePet.Rendering.ImageCache.LoadAsync(url);
            var img = MaplePet.Rendering.ImageCache.CropCharacterCanvas(bmp);
            if (img is not null) target.Source = img;
        }
        catch { /* ignore image failures */ }
    }

    private void OpenUrl(string url)
    {
        try { TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); }
        catch { /* ignore bad URLs */ }
    }

    private static string Trim(string title, string url)
    {
        var t = string.IsNullOrWhiteSpace(title) ? url : title;
        return t.Length <= 64 ? t : t[..64] + "…";
    }

    private void PositionAtBottomCenter()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        double s = screen.Scaling;
        var wa = screen.WorkingArea;
        int physW = (int)Math.Round(BarWidth * s);
        int physH = (int)Math.Round((Bounds.Height > 0 ? Bounds.Height : 60) * s);
        int margin = (int)Math.Round(BottomMarginLogical * s);

        int x = wa.X + (wa.Width - physW) / 2;
        int y = wa.Y + wa.Height - physH - margin;
        Position = new PixelPoint(x, y);
    }
}
