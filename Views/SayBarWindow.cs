using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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
    private const double CommandHoldSeconds = 12; // default time a command result holds the pet still (overridable per command)

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
    private readonly ListBox _commandMenu;
    private readonly Border _commandMenuHost;

    private bool _everActivated;
    private bool _busy;
    // True once the user has arrowed into the command dropdown — Enter then accepts the highlighted command
    // instead of submitting (so typing a full command and pressing Enter still runs it). Reset on every edit.
    private bool _menuNavigated;
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

        // --- command dropdown (above the input, shown while typing a '/' command name) ---
        _commandMenu = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            MaxHeight = 240, // scrolls if the command list ever outgrows the bar
            ItemTemplate = new FuncDataTemplate<ChatCommands.CommandInfo>((ci, _) =>
            {
                var sp = new StackPanel { Spacing = 1 };
                sp.Children.Add(new TextBlock
                {
                    Text = ci.Usage,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                    Foreground = FrostTheme.TextPrimary,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = ci.Help,
                    FontSize = 11,
                    Foreground = FrostTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                });
                return sp;
            }, supportsRecycling: true),
        };
        // A click/tap on a row accepts it (selection updates on press, before this fires).
        _commandMenu.Tapped += (_, _) =>
        {
            if (_commandMenu.SelectedItem is ChatCommands.CommandInfo ci) AcceptCommand(ci);
        };
        var menuStack = new StackPanel { Margin = new Thickness(14, 10, 14, 2) };
        menuStack.Children.Add(new TextBlock
        {
            Text = "Commands",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = FrostTheme.TextSecondary,
            Margin = new Thickness(8, 0, 0, 4),
        });
        menuStack.Children.Add(_commandMenu);
        _commandMenuHost = new Border { Child = menuStack, IsVisible = false };

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
        _input.TextChanged += (_, _) => UpdateCommandMenu();

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
        DockPanel.SetDock(_commandMenuHost, Dock.Bottom); // sits directly above the input, under any history
        dock.Children.Add(row);
        dock.Children.Add(_commandMenuHost);
        dock.Children.Add(_historyHost); // last = fills the remaining top space

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
        UpdateCommandMenu(); // reflect any text left over from a previous open
        Dispatcher.UIThread.Post(() => _everActivated = true);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // While the command dropdown is up it owns the arrow/Tab/Esc keys (and Enter once you've arrowed
        // into it), so it behaves like an autocomplete instead of moving the caret or dismissing the bar.
        if (MenuOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveMenuSelection(1); e.Handled = true; return;
                case Key.Up: MoveMenuSelection(-1); e.Handled = true; return;
                case Key.Tab:
                    if (_commandMenu.SelectedItem is ChatCommands.CommandInfo tab) AcceptCommand(tab);
                    e.Handled = true; // never let Tab move focus out of the bar while the menu is up
                    return;
                case Key.Escape: HideCommandMenu(); e.Handled = true; return;
                case Key.Enter:
                    // Accept the highlighted command only if the user actually navigated the list; otherwise
                    // fall through so a typed-out command (e.g. "/ssc") still submits on Enter.
                    if (_menuNavigated && _commandMenu.SelectedItem is ChatCommands.CommandInfo ent)
                    { AcceptCommand(ent); e.Handled = true; return; }
                    break;
            }
        }

        if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideRequested?.Invoke(); e.Handled = true; }
    }

    // ---- command dropdown --------------------------------------------------------

    private bool MenuOpen => _commandMenuHost.IsVisible;

    /// <summary>Refresh the '/' command dropdown for the current input. It shows only while the user is still
    /// typing the command NAME — i.e. the first character is '/' and no space has been typed yet (a space
    /// means the name is settled and arguments are being entered). The list is filtered by the typed prefix.</summary>
    private void UpdateCommandMenu()
    {
        _menuNavigated = false; // any edit drops out of keyboard-navigation mode
        var text = _input.Text ?? "";
        if (text.Length == 0 || text[0] != ChatCommands.Prefix || text.Contains(' '))
        {
            HideCommandMenu();
            return;
        }

        string token = text[1..];
        var matches = _commands.Commands
            .Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            HideCommandMenu();
            return;
        }

        _commandMenu.ItemsSource = matches;
        _commandMenu.SelectedIndex = 0;
        _commandMenuHost.IsVisible = true;
    }

    private void HideCommandMenu()
    {
        _commandMenuHost.IsVisible = false;
        _menuNavigated = false;
    }

    private void MoveMenuSelection(int delta)
    {
        int count = _commandMenu.ItemCount;
        if (count == 0) return;
        int i = Math.Clamp(_commandMenu.SelectedIndex + delta, 0, count - 1);
        _commandMenu.SelectedIndex = i;
        _commandMenu.ScrollIntoView(i);
        _menuNavigated = true;
    }

    /// <summary>Fill the bar with the chosen command and a trailing space (ready for arguments; the space
    /// also dismisses the menu), keep focus in the input, and put the caret at the end.</summary>
    private void AcceptCommand(ChatCommands.CommandInfo cmd)
    {
        _input.Text = $"{ChatCommands.Prefix}{cmd.Name} ";
        _input.CaretIndex = _input.Text.Length;
        HideCommandMenu();
        _input.Focus();
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
                // A command can ask for text to be put on the clipboard (e.g. /ssc, /asc). The write is a UI
                // concern (needs a TopLevel), so the command only carries the text and the say bar copies it.
                if (!string.IsNullOrEmpty(cmd.ClipboardText)) await SetClipboardAsync(cmd.ClipboardText);
                // /clear wipes the conversation: reset the agent's context AND the visible bubbles (including
                // the "/clear" line just added above). The say bar owns both, so the command only flags intent.
                if (cmd.ClearHistory) { _agent()?.Reset(); ClearHistory(); }
                var ctext = string.IsNullOrWhiteSpace(cmd.Text) ? "…" : cmd.Text;
                // A command link (e.g. /rank's MapleRanks page) shows as a clickable line in the pet's bubble
                // (and, when open, in history). The bubble link is ALWAYS shown; it's only made click-hittable
                // while no focusable window is up (see PetWindow.TickInput), so it can't swallow presses meant
                // for an open say bar.
                // Command results hold the pet still (freezeMovement) so they stay put to read — until the user
                // pokes the pet or this per-command timer elapses (default CommandHoldSeconds; /esfera asks for
                // longer to read its guide).
                double secs = cmd.HoldSeconds ?? CommandHoldSeconds;
                _ = control?.Say(ChatMarkup.ToSpoken(ctext), secs, cmd.Link?.Url, cmd.Link?.Title, cmd.ImageUrl, freezeMovement: true);
                if (keepOpen)
                {
                    // After /clear the history is intentionally empty — don't re-add a bubble for the result.
                    if (!cmd.ClearHistory) AddAssistantBubble(ctext, cmd.Sources, cmd.IsError, cmd.Link, cmd.ImageUrl);
                    _input.Focus();
                }
                return;
            }

            var agent = _agent();
            bool chat = _cfg.EnableChatbot && agent is not null && control is not null && SafeChatConfigured();

            if (!chat)
            {
                // Chatbot off (or unconfigured): the pet just repeats the text. Once in a while nudge the
                // user that a real reply is waiting behind the setting — only 30% of the time, so it stays a
                // gentle hint rather than nagging on every line.
                var say = Random.Shared.NextDouble() < 0.30
                    ? $"{text}\n(enable chatbot in the settings to make me reply)"
                    : text;
                control?.Say(say);      // plain "say" — the pet repeats the text
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
            var spoken = ChatMarkup.ToSpoken(reply); // speech bubble renders raw text — drop the * markers
            _ = control!.Say(spoken, ChatSpeechSeconds(spoken));
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

    /// <summary>Wipe all conversation bubbles (used by <c>/clear</c>). The agent's own context is reset
    /// separately by the caller; this just empties the visible panel, which then collapses itself.</summary>
    private void ClearHistory()
    {
        _history.Children.Clear();
        UpdateHistoryVisibility();
    }

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

        // Optional image (a /rank character canvas, or a /esfera guide), shown atop the card and loaded async
        // (cached). DownOnly means the cap only shrinks oversized images (the guide) — a small avatar stays
        // its native size — so this matches the speech bubble's ImgMax.
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, // clamp big images, never upscale
                MaxWidth = 420,
                MaxHeight = 420,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
            };
            body.Children.Add(img);
            LoadImageInto(img, imageUrl!);
        }

        // Render light inline markdown (**bold**, *italic*) and make links clickable; the body text stays
        // selectable. The pet's speech bubble gets the stripped/plain version (see ToSpoken at the call site).
        body.Children.Add(ChatMarkup.BuildBlock(text, isError ? FrostTheme.StatusError : FrostTheme.TextPrimary, OpenUrl));

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

    /// <summary>Load <paramref name="url"/> (cached, remote or bundled) and set it as the image's source once
    /// ready. Fire and forget; failures leave the placeholder blank.</summary>
    private static async void LoadImageInto(Image target, string url)
    {
        try
        {
            var img = await MaplePet.Rendering.ImageCache.LoadBubbleImageAsync(url);
            if (img is not null) target.Source = img;
        }
        catch { /* ignore image failures */ }
    }

    private void OpenUrl(string url)
    {
        try { TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); }
        catch { /* ignore bad URLs */ }
    }

    /// <summary>Write <paramref name="text"/> to the system clipboard via this window's TopLevel. The bar is a
    /// persistent singleton (only hidden, never closed), so the platform clipboard stays reachable even after
    /// a folded-mode command has dismissed it. Failures are swallowed — a copy command should never crash.</summary>
    private async Task SetClipboardAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        }
        catch { /* ignore clipboard failures */ }
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
