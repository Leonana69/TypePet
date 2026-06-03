using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MaplePet.Views;

/// <summary>
/// A slim, macOS-Spotlight-style input bar that floats near the bottom-center of the primary screen.
/// The user types one line and presses Enter to make the pet say it; Escape — or clicking away (the
/// window deactivating) — dismisses it. Opened by double-clicking the pet or the configured global
/// hotkey (see <c>PetWindow</c> / <c>App.ShowSayInput</c>).
///
/// Unlike the click-through pet overlay this is an ordinary focusable window, so it takes keyboard
/// input normally. It reuses the frosted-glass acrylic look from <see cref="FrostedWindow"/> but drops
/// the title-bar chrome (FrostedWindow exposes no way to hide it), so it is a sibling <see cref="Window"/>
/// rather than a subclass.
/// </summary>
public sealed class SayBarWindow : Window
{
    private const double BarWidth = 560;
    private const double BarHeight = 60;
    private const double BottomMarginLogical = 120; // how far up from the working-area bottom it floats

    private readonly TextBox _input;
    private readonly Action<string> _onSubmit;
    private bool _everActivated; // so a spurious early Deactivated can't close us before we've opened

    public SayBarWindow(Action<string> onSubmit)
    {
        _onSubmit = onSubmit;

        // Frosted-glass plumbing copied from FrostedWindow (minus the title bar): a borderless client
        // extended under the chrome, acrylic blur, with DWM rounding + shadow on Windows 11.
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
        WindowStartupLocation = WindowStartupLocation.Manual; // positioned ourselves, bottom-center
        ShowInTaskbar = false;
        Topmost = true; // stay visible above the focused app, like a launcher
        Width = BarWidth;
        Height = BarHeight;

        _input = new TextBox
        {
            Watermark = "Say something…",
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            MaxLength = 500,
        };
        _input.Classes.Add("sayInput");
        _input.KeyDown += OnInputKeyDown;

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(18, 0, 18, 0),
        };

        // The app's mushroom logo on the left (skipped if the asset won't load).
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
            content.Children.Add(img);
        }

        Grid.SetColumn(_input, 1);
        content.Children.Add(_input);

        // Fill the (rectangular) client with acrylic; DWM rounds + shadows the window on Win11.
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

        Content = new Panel { Children = { acrylic, content } };

        Activated += (_, _) => _everActivated = true;
        Deactivated += (_, _) => { if (_everActivated) Close(); };
    }

    /// <summary>Re-focus the text field (used when an open bar is re-summoned).</summary>
    public void FocusInput() => _input.Focus();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionAtBottomCenter();
        _input.Focus();
        // Arm click-away dismissal even if the OS never delivers Activated (e.g. it refused foreground).
        // Deferred, so a spurious synchronous open-time Deactivated can't close the bar before it shows.
        Dispatcher.UIThread.Post(() => _everActivated = true);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        var text = _input.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            try { _onSubmit(text); }
            catch { /* the say channel is best-effort; never let it crash the bar */ }
        }
        Close();
    }

    /// <summary>Place the bar horizontally centered and floating above the bottom of the PRIMARY
    /// screen's working area (clear of the taskbar). <see cref="Window.Position"/> is in PHYSICAL px
    /// (same space as the screen rect), while Width/Height are logical — so the logical size and margin
    /// are multiplied by the screen's scaling to get the physical offset.</summary>
    private void PositionAtBottomCenter()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        double s = screen.Scaling;
        var wa = screen.WorkingArea;
        int physW = (int)Math.Round(Width * s);
        int physH = (int)Math.Round(Height * s);
        int margin = (int)Math.Round(BottomMarginLogical * s);

        int x = wa.X + (wa.Width - physW) / 2;
        int y = wa.Y + wa.Height - physH - margin;
        Position = new PixelPoint(x, y);
    }
}
