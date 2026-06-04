using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MaplePet.Api;
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
    private readonly CharacterStore _store;
    private readonly GameLoop _loop;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _topmostTimer; // re-asserts the overlay's topmost z-order (see ApplyClickThrough)

    private IWindowTracker? _tracker;
    private WindowsWindowTracker? _winTracker;
    private ScreenSpace _screen = new(0, 0, 1);
    private World? _world;
    private PetController? _pet;
    private CharacterSprites? _sprites;
    private CharacterAnimator? _animator;
    private PetControlService? _control;

    private string? _speechText;        // active speech bubble (control API's Say), drawn over the pet
    private double _speechRemainingMs;  // countdown until the bubble clears

    /// <summary>The programmatic control surface for this pet (LLM / MCP). Available after
    /// <see cref="ControlReady"/> fires.</summary>
    public IPetControl? Control => _control;

    /// <summary>Raised once the pet and its <see cref="Control"/> facade are constructed (end of
    /// <see cref="OnOpened"/>), so the app can start the control transport against a live facade.</summary>
    public event Action? ControlReady;

    private nint _hwnd;
    private bool _overlayHidden; // true while the overlay is hidden behind a fullscreen app
    private bool _lmbPrev;     // left button state on the previous tick
    private bool _wasDragging; // drag state on the previous tick, to fire begin/end once per session
    private readonly Random _rng = new(); // picks the per-drag face expression
    private MouseClickBlocker? _clickBlocker; // eats the grab click so it doesn't hit the window behind
    private HotkeyListener? _hotkey;          // global keyboard hook that opens the say-input bar

    // Click/double-click synthesis on the (click-through) pet — derived from the polled drag edges,
    // since the overlay never receives Avalonia pointer events.
    private long _downTickMs;     // when the current press on the pet began
    private Vec2 _downCursor;     // cursor at that press, to tell an in-place click from a drag
    private bool _movedThisPress; // the pointer ever moved past the click threshold during this press
    private long _lastClickUpMs;  // when the previous in-place click released (for double-click pairing)
    private Vec2 _lastClickPos;
    private const double ClickMaxMoveLogicalPx = 6; // a press+release that moves less than this is a "click"
    private const long ClickMaxHoldMs = 600;        // ...and is released within this long (else it's a hold)

    /// <summary>Raised (on the UI thread) when the user asks to open the say-input bar — by
    /// double-clicking the pet or pressing the configured global hotkey.</summary>
    public event Action? SayInputRequested;

    /// <summary>While true, the overlay stops re-asserting its topmost z-order, so a focusable window
    /// opened above it (the say-input bar) isn't pushed behind the pet. Set by the app.</summary>
    public bool SuppressOverlayTopmost { get; set; }

    /// <summary>True while the overlay is hidden behind a fullscreen app — the game loop is frozen, so
    /// nothing draws and the speech-bubble countdown doesn't tick. The app checks this before arming a
    /// transient bubble (e.g. the second-launch greeting) that would otherwise resurface, stale, only
    /// when the overlay is later restored.</summary>
    public bool IsOverlayHidden => _overlayHidden;

    // Exists only so Avalonia's runtime XAML loader can reach this window's resource; the
    // app always constructs it via the overload below (with the shared instances).
    public PetWindow() : this(Settings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json")), new CharacterStore()) { }

    public PetWindow(Settings settings, CharacterStore store)
    {
        InitializeComponent();
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _cfg = settings;
        _store = store;
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
        // Keep the pet clear of the screen edges (it's drawn tall, above its feet): don't roam
        // onto platforms within 150px of the bottom edge, nor within 150px of the top (or its
        // head would be drawn off-screen above).
        _pet.RoamMaxY = Height - 150;
        _pet.RoamMinY = 150;

        // Load the currently selected character (a user import from the store, or the bundled
        // Body+Head default). CharacterLoader falls back to the default if the footage can't be
        // decoded; if even that fails, the renderer draws the placeholder shape. The live pet decodes
        // its action poses too (LivePoses) while the grab box stays sized to the played poses.
        _sprites = CharacterLoader.Load(_store, _cfg.CurrentCharacterId,
            hitTestPoses: CharacterAnimator.ActivePoses, posesToLoad: CharacterAnimator.LivePoses, loadExpressions: true);
        _animator = new CharacterAnimator();
        View.Sprites = _sprites;
        View.Animator = _animator;
        View.ShowDebug = _cfg.ShowOverlay;

        // The programmatic control facade reads the pet through accessors so it always sees the live
        // (swappable) instances. All its calls marshal onto this UI thread.
        _control = new PetControlService(
            () => _pet, () => _animator, () => _sprites, () => _world,
            () => new MaplePet.Engine.Rect(0, 0, Width, Height),
            () =>
            {
                var entry = _store.Get(_cfg.CurrentCharacterId);
                return (_cfg.CurrentCharacterId, entry?.DisplayName ?? "Default");
            },
            SetSpeech);

        PollWorld(); // prime the world before the first frame (may hide the overlay if a fullscreen app is already up)

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, _cfg.WorldPollHz)),
        };
        _pollTimer.Tick += (_, _) => PollWorld();
        _pollTimer.Start();
        if (!_overlayHidden) _loop.Start(); // PollWorld stops the loop while hidden; don't override that

        ControlReady?.Invoke(); // the control facade is live; the app can start the MCP transport now

        if (AppState.SmokeSeconds > 0)
            ScheduleSmokeExit(AppState.SmokeSeconds);
    }

    protected override void OnClosed(EventArgs e)
    {
        _loop.Stop();
        _pollTimer?.Stop();
        _topmostTimer?.Stop();
        _clickBlocker?.Dispose();
        _hotkey?.Dispose();
        _sprites?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Swap the pet's visual to a different character at runtime (from the character picker). Updates
    /// the drag hit-box too, since it's measured from the sprites. A null value means "footage failed
    /// to load" — the renderer then falls back to the placeholder shape. The animator is unchanged:
    /// poses are keyed by name and shared across characters.
    /// </summary>
    public void SetCharacter(CharacterSprites? sprites)
    {
        if (ReferenceEquals(_sprites, sprites)) return;
        var old = _sprites;
        _sprites = sprites;
        View.Sprites = sprites;
        old?.Dispose(); // free the previous character's bitmaps (each load owns its own instances)
        _control?.NotifyCapabilitiesChanged(); // available actions/expressions are character-specific
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

            // Global hotkey that pops up the say-input bar from anywhere. Installed on the UI thread
            // next to the mouse hook so its callback is delivered here too; the configured chord comes
            // from settings (and is re-applied live via SetSayHotkey).
            _hotkey = new HotkeyListener();
            _hotkey.Triggered += RequestSayInput;
            _hotkey.Install();
            SetSayHotkey(_cfg.SayInputHotkey);

            // Keep the pet above other topmost windows. Avalonia sets WS_EX_TOPMOST once, but
            // activating a topmost app (e.g. a borderless-fullscreen game) raises it above us within
            // the topmost band, and this overlay never takes focus to recover. Re-assert a couple of
            // times a second; it's invisible (no move/size/activate) and a no-op under true exclusive
            // fullscreen, where DWM isn't compositing the desktop to that display anyway.
            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _topmostTimer.Tick += (_, _) => { if (!SuppressOverlayTopmost && !_overlayHidden) WindowsInterop.RaiseToTop(_hwnd); };
            _topmostTimer.Start();
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
        {
            _downTickMs = Environment.TickCount64; // press time + point, to tell an in-place click from a drag
            _downCursor = cursor;
            _movedThisPress = false;
            _pet.BeginDrag();
        }

        if (_pet.IsDragging)
        {
            if (lmb)
            {
                if (TryCursorLogical(out var c))
                {
                    if (Dist(c, _downCursor) > ClickMaxMoveLogicalPx) _movedThisPress = true;
                    _pet.DragTo(c);
                }
            }
            else
            {
                if (TryCursorLogical(out var up)) DetectDoubleClick(up);
                _pet.EndDrag();
            }
        }
        _lmbPrev = lmb;
    }

    /// <summary>
    /// On releasing the pet, decide whether it was an in-place click (barely moved) and, if a second
    /// such click lands within the OS double-click window, fire <see cref="SayInputRequested"/>. The
    /// overlay is click-through so this is the only signal we have — a drag (moved past the threshold)
    /// resets the pairing so it never counts as a click.
    /// </summary>
    private void DetectDoubleClick(Vec2 up)
    {
        long now = Environment.TickCount64;
        // A click is a press+release that never moved past the threshold and wasn't a long hold;
        // a drag (moved, e.g. one that loops back near its start) or a deliberate hold is not.
        bool isClick = !_movedThisPress
            && Dist(up, _downCursor) <= ClickMaxMoveLogicalPx
            && now - _downTickMs <= ClickMaxHoldMs;
        if (!isClick)
        {
            _lastClickUpMs = 0;
            return;
        }

        if (_lastClickUpMs != 0
            && now - _lastClickUpMs <= WindowsInterop.DoubleClickTimeMs()
            && Dist(up, _lastClickPos) <= ClickMaxMoveLogicalPx * 2)
        {
            _lastClickUpMs = 0; // consume the pair
            RequestSayInput();
        }
        else
        {
            _lastClickUpMs = now; // first click of a potential pair
            _lastClickPos = up;
        }
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Apply a hotkey gesture (e.g. "Ctrl+Alt+Space") to the global listener. Called on
    /// startup and whenever the user edits it in Settings. A malformed or modifier-less gesture
    /// disables the hotkey rather than binding a bare key system-wide.</summary>
    public void SetSayHotkey(string gesture)
    {
        if (_hotkey is null) return; // not on Windows, or the hook isn't installed
        if (HotkeyGesture.IsBindable(gesture)
            && HotkeyGesture.TryParse(gesture, out var mods, out var key)
            && HotkeyGesture.KeyToVirtualKey(key) is int vk)
        {
            _hotkey.SetHotkey(vk,
                mods.HasFlag(KeyModifiers.Control), mods.HasFlag(KeyModifiers.Alt),
                mods.HasFlag(KeyModifiers.Shift), mods.HasFlag(KeyModifiers.Meta));
        }
        else
        {
            _hotkey.Disable(); // malformed or modifier-less — don't hijack a bare key globally
        }
    }

    /// <summary>Temporarily disable the global hotkey (used while the user is capturing a new chord in
    /// Settings, so the live hook doesn't swallow the very keys being captured). Re-armed by a
    /// subsequent <see cref="SetSayHotkey"/>.</summary>
    public void SuspendSayHotkey() => _hotkey?.Disable();

    /// <summary>Ask the app to open the say-input bar. Posted to the dispatcher so it never runs inline
    /// on the keyboard-hook callback (which must return immediately) or re-enter the game tick.</summary>
    private void RequestSayInput()
        => Dispatcher.UIThread.Post(() => SayInputRequested?.Invoke());

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

        // Hide the pet while a borderless / exclusive-fullscreen app (a game or video) is foreground,
        // so it never sits on top of it. Re-checked every poll, so the pet returns the moment the user
        // alt-tabs back to the desktop. While hidden there's nothing to draw, so skip the world rebuild.
        bool wantHidden = _cfg.HideWhenFullscreen && _tracker.IsForegroundFullscreen();
        if (wantHidden != _overlayHidden) SetOverlayHidden(wantHidden);
        if (_overlayHidden) return;

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

    /// <summary>
    /// Hide or restore the whole overlay when a fullscreen app takes/relinquishes the foreground. When
    /// hidden we freeze the game loop (the pet stops walking on now-stale geometry and the overlay stops
    /// spending frames), clear the click hook's pet rect (so a click meant for the game is never eaten),
    /// and SW_HIDE the native window. Restoring shows it without activating (preserving WS_EX_NOACTIVATE),
    /// re-asserts topmost, and resumes the loop. The world poll keeps running throughout, so the next
    /// poll after the app exits restores the pet and a fresh geometry capture follows.
    /// </summary>
    private void SetOverlayHidden(bool hidden)
    {
        _overlayHidden = hidden;

        if (hidden)
        {
            // Tear down any in-progress grab before the pet vanishes: drop it, forget the click hook's
            // swallowed press (so a button-up meant for the fullscreen app behind isn't eaten), and reset
            // the per-drag input edges so a stale drag can't resume when we show again.
            if (_pet?.IsDragging == true) _pet.EndDrag();
            _clickBlocker?.SetPetRect(false, 0, 0, 0, 0);
            _clickBlocker?.ResetSwallow();
            _lmbPrev = false;
            if (_wasDragging) { _wasDragging = false; _animator?.SetTransientExpression(null); }

            _loop.Stop();
            if (OperatingSystem.IsWindows()) WindowsInterop.Hide(_hwnd);
        }
        else
        {
            // Displays may have changed while we were hidden — a fullscreen game often switches
            // resolution — so re-measure the overlay's span and coordinate origin before showing again,
            // otherwise cursor hit-testing and the click hook's pet rect would be misaligned.
            LayoutOverlay();
            if (_pet is not null) { _pet.RoamMaxY = Height - 150; _pet.RoamMinY = 150; }

            if (OperatingSystem.IsWindows())
            {
                WindowsInterop.ShowNoActivate(_hwnd);
                WindowsInterop.RaiseToTop(_hwnd);
            }
            _loop.Start();
        }
    }

    private void OnTick(double dt)
    {
        UpdateInput();
        View.ShowDebug = _cfg.ShowOverlay; // live-toggled from Settings
        if (_pet is not null && _world is not null)
        {
            UpdateDragExpression(_pet.IsDragging); // react to the grab with a (held) random face
            _pet.Update(_world, dt);
            _animator?.Update(_sprites, _pet.State, dt);

            // Arbitration between commanded actions and autonomy (precedence: drag > action > autonomy):
            if (_pet.IsDragging)
            {
                // A grab interrupts any commanded action; let the pet flail and re-roam after release.
                if (_animator?.Action is not null) { _animator.SetAction(null); _pet.ResumeRoaming(); }
            }
            else if (_animator?.ActionJustCompleted() == true)
            {
                _pet.ResumeRoaming(); // a one-shot action finished — autonomy may resume
            }

            View.Pet = _pet;
        }

        // Speech bubble lifetime.
        if (_speechRemainingMs > 0)
        {
            _speechRemainingMs -= dt * 1000.0;
            if (_speechRemainingMs <= 0) _speechText = null;
        }
        View.Speech = _speechText;

        PublishPetHitBox(); // keep the click hook's pet rect current
        View.InvalidateVisual();
    }

    /// <summary>Set or clear the speech bubble (called by the control API's Say, on the UI thread).</summary>
    private void SetSpeech(string? text, double? seconds)
    {
        _speechText = string.IsNullOrWhiteSpace(text) ? null : text;
        _speechRemainingMs = _speechText is null ? 0 : (seconds is double s && s > 0 ? s * 1000.0 : 4000);
    }

    /// <summary>
    /// Drive the pet's face expression off the drag state: on grab, switch to one random expression and
    /// hold it for the whole drag; on release, restore the neutral face. The pick happens once per drag
    /// session (on the false→true edge), so the face doesn't flicker between expressions while held.
    /// </summary>
    private void UpdateDragExpression(bool dragging)
    {
        if (dragging == _wasDragging) return;
        _wasDragging = dragging;
        // Use the transient channel so the grab face overlays — and on release reverts to — any
        // base expression set via the control API, instead of wiping it to neutral.
        _animator?.SetTransientExpression(dragging ? PickRandomExpression() : null);
    }

    /// <summary>A random expression name from the worn character, excluding the neutral "default" (so the
    /// grab visibly changes the face). Null when the character has no expressions to show.</summary>
    private string? PickRandomExpression()
    {
        if (_sprites is not { HasExpressions: true } s) return null;
        var names = s.ExpressionNames
            .Where(n => !n.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return names.Count == 0 ? null : names[_rng.Next(names.Count)];
    }

    private void ScheduleSmokeExit(double seconds)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
