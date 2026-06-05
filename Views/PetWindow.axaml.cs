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
using MaplePet.Platform.Abstractions;
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

    private IPlatformServices? _plat; // the OS-specific service bundle (window tracker, overlay, input, hotkey)
    private IOverlayEffects? _overlay; // click-through + topmost z-order
    private IWindowTracker? _tracker;
    private ScreenSpace _screen = new(0, 0, 1);
    private World? _world;
    private PetController? _pet;
    private CharacterSprites? _sprites;
    private CharacterAnimator? _animator;
    private PetControlService? _control;

    private string? _speechText;        // active speech bubble (control API's Say), drawn over the pet
    private double _speechRemainingMs;  // countdown until the bubble clears
    private string? _speechLinkUrl;     // optional URL the bubble's link line opens (cleared with the bubble)
    private string? _speechLinkLabel;   // the bubble link's display label

    /// <summary>The programmatic control surface for this pet (LLM / MCP). Available after
    /// <see cref="ControlReady"/> fires.</summary>
    public IPetControl? Control => _control;

    /// <summary>Raised once the pet and its <see cref="Control"/> facade are constructed (end of
    /// <see cref="OnOpened"/>), so the app can start the control transport against a live facade.</summary>
    public event Action? ControlReady;

    private bool _overlayHidden; // true while the overlay is hidden behind a fullscreen app
    private bool _wasDragging; // drag state on the previous tick, to fire begin/end once per session
    private readonly Random _rng = new(); // picks the per-drag face expression
    private IPetInput? _input;                // drag/click gestures (Windows: polled cursor + click hook)
    private IGlobalHotkey? _hotkey;           // global hotkey that opens the input bar (may be unsupported)

    /// <summary>Raised (on the UI thread) when the user asks to open the input bar — by double-clicking
    /// the pet or pressing the configured global hotkey.</summary>
    public event Action? SayInputRequested;

    /// <summary>While true, the overlay stops re-asserting its topmost z-order, so a focusable window
    /// opened above it (the say-input bar) isn't pushed behind the pet. Set by the app.</summary>
    public bool SuppressOverlayTopmost
    {
        get => _overlay?.SuppressTopmost ?? false;
        set { if (_overlay is not null) _overlay.SuppressTopmost = value; }
    }

    /// <summary>True while the overlay is hidden behind a fullscreen app — the game loop is frozen, so
    /// nothing draws and the speech-bubble countdown doesn't tick. The app checks this before arming a
    /// transient bubble (e.g. the second-launch greeting) that would otherwise resurface, stale, only
    /// when the overlay is later restored.</summary>
    public bool IsOverlayHidden => _overlayHidden;

    // Exists only so Avalonia's runtime XAML loader can reach this window's resource; the
    // app always constructs it via the overload below (with the shared instances).
    public PetWindow() : this(
        Settings.Load(PlatformServices.AppPaths.SettingsPath),
        new CharacterStore(PlatformServices.AppPaths.CharactersRoot)) { }

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
        SetupPlatform();

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
        _overlay?.Dispose();
        _input?.Dispose();
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

    /// <summary>
    /// Resolve the OS platform services (window tracker, overlay effects, input, hotkey) and wire them
    /// to the pet. The factory picks the Windows or macOS bundle; PetWindow speaks only the abstractions
    /// and logical pixels, so there are no OS branches here. The pet is constructed just after this, so
    /// the input callbacks read the <c>_pet</c> field lazily.
    /// </summary>
    private void SetupPlatform()
    {
        _plat = PlatformServices.Create(this);
        _tracker = _plat.WindowTracker;
        _overlay = _plat.OverlayEffects;
        _input = _plat.Input;
        _hotkey = _plat.Hotkey;

        // Make the overlay click-through and pin it to the top of the z-order (the impl owns any
        // periodic re-assert). On a missing native handle this no-ops; in practice it's valid here.
        nint handle = TryGetPlatformHandle()?.Handle ?? 0;
        _overlay.ConfigureOverlay(handle);

        // Drag/click are delivered as gestures (Windows polls a cursor + low-level mouse hook because
        // the overlay is permanently click-through and never gets Avalonia pointer events; macOS toggles
        // click-through and uses native pointer events).
        _input.Start(new PetInputCallbacks(
            OnDragBegin: _ => _pet?.BeginDrag(),
            OnDragMove: c => _pet?.DragTo(c),
            OnDragEnd: () => _pet?.EndDrag(),
            OnSayRequested: RequestSayInput,
            OnLinkActivated: OpenSpeechLink));

        // Global hotkey that pops up the say-input bar from anywhere (skipped where unsupported — the
        // pet can always be double-clicked to open it instead).
        if (_hotkey.IsSupported)
        {
            _hotkey.Triggered += RequestSayInput;
            _hotkey.Install();
            SetSayHotkey(_cfg.SayInputHotkey);
        }
    }

    /// <summary>Apply a hotkey gesture (e.g. "Ctrl+Alt+Space") to the global listener. Called on
    /// startup and whenever the user edits it in Settings. A malformed or modifier-less gesture
    /// disables the hotkey rather than binding a bare key system-wide.</summary>
    public void SetSayHotkey(string gesture)
    {
        if (_hotkey is null || !_hotkey.IsSupported) return; // no global hotkey on this platform
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

    /// <summary>Ask the app to open the input bar. Posted to the dispatcher so it never runs inline on the
    /// keyboard-hook callback (which must return immediately) or re-enter the game tick.</summary>
    private void RequestSayInput()
        => Dispatcher.UIThread.Post(() => SayInputRequested?.Invoke());

    /// <summary>
    /// The pet's clickable box in logical (overlay) px. Matches the drawn character: centered on
    /// CenterX, rising HeightAboveFeet above the feet (well past the small physics box); falls back to
    /// the physics box when footage didn't load. Published to the platform input layer each tick.
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

    /// <summary>Drive the platform input layer for this tick: hand it the pet's current clickable box
    /// (logical px) so it can poll the cursor/button (Windows) or toggle click-through (macOS), and
    /// raise drag/click gestures. Called at the top of <see cref="OnTick"/>, before physics moves the
    /// pet, so the poll sees the same position the original inline poll did.</summary>
    private void TickInput()
    {
        if (_input is null) return;
        bool visible = TryPetHitBoxLogical(out double l, out double t, out double r, out double b);
        var pet = visible
            ? new MaplePet.Platform.Abstractions.HitRect(true, l, t, r, b)
            : MaplePet.Platform.Abstractions.HitRect.None;

        // The bubble link's clickable box (computed by the renderer last frame, logical px). The link line is
        // always DRAWN; we only publish it as a hit target when:
        //  - the authoritative UI-thread URL is set (cleared synchronously by SetSpeech/timeout), so a stale
        //    renderer rect is never published once the link is gone (else the hook swallows an empty click);
        //  - no focusable window is up (SuppressOverlayTopmost), so the rect can't swallow a press meant for
        //    an open say bar that overlaps the bubble (history shows the same link there to click instead).
        var link = (_speechLinkUrl is not null && !SuppressOverlayTopmost && View.SpeechLinkRect is { } lr)
            ? new MaplePet.Platform.Abstractions.HitRect(true, lr.Left, lr.Top, lr.Right, lr.Bottom)
            : MaplePet.Platform.Abstractions.HitRect.None;

        _input.Tick(_screen, pet, link);
    }

    private void PollWorld()
    {
        if (_tracker is null) return;

        // Keep the coordinate origin pinned to the overlay's ACTUAL on-screen position. macOS clamps an
        // (unbundled) top-level below the menu bar, so the realized position differs from the requested
        // (minX,minY) and isn't reported via PositionChanged; aligning the origin with it keeps the pet
        // on the window edges the tracker reports. On Windows the overlay isn't moved, so Position equals
        // (minX,minY) and this is a no-op.
        _screen = new ScreenSpace(Position.X, Position.Y, _screen.Scale);

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
            _input?.CancelActiveGesture();
            if (_wasDragging) { _wasDragging = false; _animator?.SetTransientExpression(null); }

            _loop.Stop();
            _overlay?.Hide();
        }
        else
        {
            // Displays may have changed while we were hidden — a fullscreen game often switches
            // resolution — so re-measure the overlay's span and coordinate origin before showing again,
            // otherwise cursor hit-testing and the click hook's pet rect would be misaligned.
            LayoutOverlay();
            if (_pet is not null) { _pet.RoamMaxY = Height - 150; _pet.RoamMinY = 150; }

            _overlay?.ShowNoActivate();
            _loop.Start();
        }
    }

    private void OnTick(double dt)
    {
        TickInput(); // poll drag/click (Windows) or toggle click-through (macOS) + publish the pet hit box
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
            if (_speechRemainingMs <= 0) { _speechText = null; _speechLinkUrl = null; _speechLinkLabel = null; }
        }
        View.Speech = _speechText;
        View.SpeechLink = _speechLinkLabel;

        View.InvalidateVisual();
    }

    /// <summary>Set or clear the speech bubble (called by the control API's Say, on the UI thread).
    /// <paramref name="linkUrl"/>/<paramref name="linkLabel"/> optionally add a clickable link line to the
    /// bubble; both are cleared when the bubble does (here or on timeout).</summary>
    private void SetSpeech(string? text, double? seconds, string? linkUrl, string? linkLabel)
    {
        _speechText = string.IsNullOrWhiteSpace(text) ? null : text;
        _speechRemainingMs = _speechText is null ? 0 : (seconds is double s && s > 0 ? s * 1000.0 : 4000);
        bool hasLink = _speechText is not null && !string.IsNullOrWhiteSpace(linkUrl) && !string.IsNullOrWhiteSpace(linkLabel);
        _speechLinkUrl = hasLink ? linkUrl : null;
        _speechLinkLabel = hasLink ? linkLabel : null;
    }

    /// <summary>Open the active speech-bubble link in the default browser (raised by the input layer when
    /// the user clicks the bubble's link line). Guarded so a stale click can't open a cleared URL.</summary>
    private void OpenSpeechLink()
    {
        if (_speechText is null || _speechLinkUrl is not { Length: > 0 } url) return;
        try { TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); }
        catch { /* ignore bad URLs */ }
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
