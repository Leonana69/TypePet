using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace MaplePet.Views;

/// <summary>
/// Base class for the app's "frosted glass" windows. It strips the OS title bar, fills the window
/// with an acrylic-blur surface, draws a rounded clipped border, and adds a custom draggable title
/// bar with the app glyph, a title, and a close button. Derived windows (or callers, for one-off
/// dialogs) put their content in via <see cref="SetBody"/> — they must NOT set <see cref="Window.Content"/>
/// directly, since that holds the chrome. Falls back to a solid dark surface where the platform
/// can't do real transparency/blur.
/// </summary>
public class FrostedWindow : Window
{
    private readonly Border _bodyHost;
    private readonly Control _titleBar;

    public FrostedWindow(string title)
    {
        Title = title;
        Classes.Add("frosted");

        // Borderless window whose client area extends under (the now-hidden) system chrome, so the
        // acrylic + our own title bar own the whole surface. DWM still rounds the corners and casts a
        // shadow on Windows 11; the inner clipped Border guarantees rounding everywhere else.
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
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Icon = AppIcon.WindowIcon(); // taskbar button

        _titleBar = BuildTitleBar(title);

        _bodyHost = new Border();

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        Grid.SetRow(_titleBar, 0);
        Grid.SetRow(_bodyHost, 1);
        content.Children.Add(_titleBar);
        content.Children.Add(_bodyHost);

        // Fill the whole (rectangular) client with acrylic and let DWM round + shadow the window.
        // We deliberately do NOT round/clip here ourselves: a second rounded border at a different
        // radius than DWM's produced a visible double-corner artifact.
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

        base.Content = new Panel { Children = { acrylic, content } };
    }

    /// <summary>Place a control in the window body (below the title bar).</summary>
    protected void SetBody(Control body) => _bodyHost.Child = body;

    /// <summary>Same as <see cref="SetBody"/>, for callers that use <see cref="FrostedWindow"/>
    /// directly as a one-off dialog rather than subclassing it.</summary>
    public void SetDialogBody(Control body) => SetBody(body);

    private Control BuildTitleBar(string title)
    {
        // The app's mushroom icon as the title-bar logo (falls back to a teal square if it won't load).
        Control logo;
        if (AppIcon.Bitmap is { } iconBmp)
        {
            var img = new Image
            {
                Width = 24,
                Height = 24,
                Source = iconBmp,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // High-quality downscale from the 512px source so the small logo stays crisp.
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
            logo = img;
        }
        else
        {
            logo = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = FrostTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var titleText = new TextBlock { Text = title };
        titleText.Classes.Add("windowTitle");

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { logo, titleText },
        };

        var close = new Button { Content = "✕" }; // ✕
        close.Classes.Add("chromeClose");
        close.Click += (_, _) => Close();

        var bar = new Grid
        {
            Height = 40,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brushes.Transparent, // hit-testable so the whole strip can drag
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(close, 1);
        bar.Children.Add(left);
        bar.Children.Add(close);

        // Drag the window from the title strip, but not when the press lands on a button (e.g. close).
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            BeginMoveDrag(e);
        };

        return bar;
    }
}
