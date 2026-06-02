using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MaplePet.Engine;
using MaplePet.Platform;
using MaplePet.Platform.Windows;

namespace MaplePet.Views;

/// <summary>
/// The transparent, click-through, topmost overlay that covers the whole virtual screen.
/// Owns the game loop, the world poll timer, the window tracker, and the pet.
/// </summary>
public partial class PetWindow : Window
{
    private readonly Settings _cfg;
    private readonly GameLoop _loop;
    private DispatcherTimer? _pollTimer;

    private IWindowTracker? _tracker;
    private WindowsWindowTracker? _winTracker;
    private ScreenSpace _screen = new(0, 0, 1);
    private World? _world;
    private PetController? _pet;

    public PetWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _cfg = Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"));
        _loop = new GameLoop(_cfg.TargetFps);
        _loop.Tick += OnTick;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        LayoutOverlay();
        SetupTracker();
        ApplyClickThrough();

        _pet = new PetController(_cfg, new Vec2(30, 38));
        PollWorld(); // prime the world before the first frame

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, _cfg.WorldPollHz)),
        };
        _pollTimer.Tick += (_, _) => PollWorld();
        _pollTimer.Start();
        _loop.Start();

        if (AppState.SmokeSeconds > 0)
            ScheduleSmokeExit(AppState.SmokeSeconds);
    }

    protected override void OnClosed(EventArgs e)
    {
        _loop.Stop();
        _pollTimer?.Stop();
        base.OnClosed(e);
    }

    /// <summary>Size and position the overlay to span the whole virtual screen.</summary>
    private void LayoutOverlay()
    {
        var screens = Screens.All;
        double scale = Screens.Primary?.Scaling ?? RenderScaling;

        int minX = 0, minY = 0, maxX = 1920, maxY = 1080;
        bool first = true;
        foreach (var s in screens)
        {
            var b = s.Bounds;
            if (first)
            {
                minX = b.X; minY = b.Y; maxX = b.X + b.Width; maxY = b.Y + b.Height;
                first = false;
            }
            else
            {
                minX = Math.Min(minX, b.X);
                minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.X + b.Width);
                maxY = Math.Max(maxY, b.Y + b.Height);
            }
        }

        Position = new PixelPoint(minX, minY);
        Width = (maxX - minX) / scale;
        Height = (maxY - minY) / scale;
        _screen = new ScreenSpace(minX, minY, scale);
    }

    private void SetupTracker()
    {
        if (OperatingSystem.IsWindows())
        {
            _winTracker = new WindowsWindowTracker();
            _tracker = _winTracker;
        }
        else
        {
            _tracker = new MaplePet.Platform.MacOS.MacWindowTracker();
        }
    }

    private void ApplyClickThrough()
    {
        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;

        if (OperatingSystem.IsWindows())
        {
            WindowsInterop.MakeClickThrough(handle.Handle);
            if (_winTracker is not null)
                _winTracker.ExcludeHwnd = handle.Handle;
        }
    }

    private void PollWorld()
    {
        if (_tracker is null) return;
        try
        {
            var physical = _tracker.Capture();
            var logical = _screen.ToLogical(physical);
            _world = WorldModel.Build(logical);
            View.Geometry = logical;
            View.World = _world;
        }
        catch (Exception ex)
        {
            // Transient capture errors (a window dying mid-enumeration) must not crash the
            // overlay; the next poll recovers. Trace it so it isn't fully invisible in dev.
            System.Diagnostics.Debug.WriteLine($"[MaplePet] world poll failed: {ex.Message}");
        }
    }

    private void OnTick(double dt)
    {
        if (_pet is not null && _world is not null)
        {
            _pet.Update(_world, dt);
            View.Pet = _pet;
        }
        View.InvalidateVisual();
    }

    private void ScheduleSmokeExit(double seconds)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
