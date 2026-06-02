using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MaplePet.Engine;
using MaplePet.Platform;
using MaplePet.Platform.Windows;
using MaplePet.Rendering;

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
    private CharacterSprites? _sprites;
    private CharacterAnimator? _animator;

    private nint _hwnd;
    private bool _lmbPrev;     // left button state on the previous tick
    private MouseClickBlocker? _clickBlocker; // eats the grab click so it doesn't hit the window behind

    // Exists only so Avalonia's runtime XAML loader can reach this window's resource; the
    // app always constructs it via the Settings overload below (with the shared instance).
    public PetWindow() : this(Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"))) { }

    public PetWindow(Settings settings)
    {
        InitializeComponent();
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _cfg = settings;
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

        // Load the MapleStory character footage. Prefer the full equipped export under
        // Assets/footage; fall back to the bundled Body+Head default character that always ships.
        // If both fail, the renderer falls back to the placeholder shape so the overlay still works.
        _sprites = CharacterSprites.Load(hitTestPoses: CharacterAnimator.ActivePoses)
                   ?? CharacterSprites.Load(footageDir: "Assets/DefaultCharacter", hitTestPoses: CharacterAnimator.ActivePoses);
        _animator = new CharacterAnimator();
        View.Sprites = _sprites;
        View.Animator = _animator;
        View.ShowDebug = _cfg.ShowOverlay;

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
        _clickBlocker?.Dispose();
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
            _hwnd = handle.Handle;
            WindowsInterop.MakeClickThrough(_hwnd);
            if (_winTracker is not null)
                _winTracker.ExcludeHwnd = _hwnd;

            // The overlay stays click-through (so it never occludes video); this low-level hook
            // swallows the left click only when it lands on the pet, so grabbing the pet doesn't
            // also click the window behind it.
            _clickBlocker = new MouseClickBlocker();
            _clickBlocker.Install();
        }
    }

    /// <summary>
    /// Poll the global cursor + left mouse button to drive dragging. The overlay stays permanently
    /// click-through (so it never occludes hardware-accelerated video below it — making it
    /// interactive would black the video out). The grab click is instead "caught" by a low-level
    /// mouse hook (<see cref="MouseClickBlocker"/>) that eats the press/release only when it lands on
    /// the pet, so it doesn't reach the window behind. Dragging itself is driven by this poll.
    /// </summary>
    private void UpdateInput()
    {
        if (_hwnd == 0 || _pet is null || !OperatingSystem.IsWindows()) return;

        // Use the hook's button state: a click swallowed by the hook (over the pet) isn't seen by
        // GetAsyncKeyState, so the poll would miss the press and never start a drag.
        bool lmb = _clickBlocker?.LeftButtonDown ?? WindowsInterop.IsLeftButtonDown();
        bool overPet = TryCursorLogical(out var cursor) && OverPet(cursor);

        if (!_pet.IsDragging && lmb && !_lmbPrev && overPet)
            _pet.BeginDrag();

        if (_pet.IsDragging)
        {
            if (lmb)
            {
                if (TryCursorLogical(out var c)) _pet.DragTo(c);
            }
            else
            {
                _pet.EndDrag();
            }
        }
        _lmbPrev = lmb;
    }

    private bool TryCursorLogical(out Vec2 logical)
    {
        logical = default;
        if (!WindowsInterop.TryGetCursorPos(out int px, out int py)) return false;
        logical = new Vec2((px - _screen.OriginX) / _screen.Scale, (py - _screen.OriginY) / _screen.Scale);
        return true;
    }

    private bool OverPet(Vec2 p)
        => TryPetHitBoxLogical(out double l, out double t, out double r, out double b)
           && p.X >= l && p.X <= r && p.Y >= t && p.Y <= b;

    /// <summary>
    /// The pet's clickable box in logical (overlay) px. Matches the drawn character: centered on
    /// CenterX, rising HeightAboveFeet above the feet (well past the small physics box); falls back to
    /// the physics box when footage didn't load. Used for both hover hit-testing and the click hook.
    /// </summary>
    private bool TryPetHitBoxLogical(out double left, out double top, out double right, out double bottom)
    {
        left = top = right = bottom = 0;
        if (_pet is null) return false;
        const double margin = 5;

        if (_sprites is { } s)
        {
            left = _pet.CenterX - s.HalfWidth - margin;
            right = _pet.CenterX + s.HalfWidth + margin;
            top = _pet.FeetY - s.HeightAboveFeet - margin;
            bottom = _pet.FeetY + margin;
        }
        else
        {
            left = _pet.Pos.X - margin;
            right = _pet.Pos.X + _pet.Size.X + margin;
            top = _pet.Pos.Y - margin;
            bottom = _pet.Pos.Y + _pet.Size.Y + margin;
        }
        return true;
    }

    /// <summary>Tell the click hook where the pet is, in physical screen px (inverse of TryCursorLogical).</summary>
    private void PublishPetHitBox()
    {
        if (_clickBlocker is null) return;
        if (TryPetHitBoxLogical(out double l, out double t, out double r, out double b))
        {
            _clickBlocker.SetPetRect(true,
                (int)Math.Floor(l * _screen.Scale + _screen.OriginX),
                (int)Math.Floor(t * _screen.Scale + _screen.OriginY),
                (int)Math.Ceiling(r * _screen.Scale + _screen.OriginX),
                (int)Math.Ceiling(b * _screen.Scale + _screen.OriginY));
        }
        else
        {
            _clickBlocker.SetPetRect(false, 0, 0, 0, 0);
        }
    }

    private void PollWorld()
    {
        if (_tracker is null) return;
        try
        {
            var physical = _tracker.Capture();
            var logical = _screen.ToLogical(physical);
            // The overlay spans the virtual screen at logical (0,0)..(Width,Height); clip the world to
            // it so off-screen parts of partially-off-screen windows aren't walkable/targetable.
            _world = WorldModel.Build(logical, new MaplePet.Engine.Rect(0, 0, Width, Height));
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
        UpdateInput();
        View.ShowDebug = _cfg.ShowOverlay; // live-toggled from Settings
        if (_pet is not null && _world is not null)
        {
            _pet.Update(_world, dt);
            _animator?.Update(_sprites, _pet.State, dt);
            View.Pet = _pet;
        }
        PublishPetHitBox(); // keep the click hook's pet rect current
        View.InvalidateVisual();
    }

    private void ScheduleSmokeExit(double seconds)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
