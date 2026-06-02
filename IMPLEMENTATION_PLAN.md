# MaplePet — Implementation Plan

A desktop pet for Windows (with a future macOS port) that lives on your screen,
walks along the taskbar and the top edges of windows, and climbs the left/right
edges of windows like ladders.

This document is the build spec. Implement it milestone by milestone, in order.
Each milestone is independently runnable and testable — do not skip ahead.

---

## 1. What we're building

A small always-on-top, transparent, click-through overlay that covers the whole
screen. A sprite (the "pet") moves around on top of everything using simple
platformer physics. The "level geometry" is derived in real time from the actual
windows currently open on the desktop.

### Behavior requirements

1. The **taskbar top edge** is the base platform. The pet walks left/right on it.
2. Each open window contributes:
   - its **top edge** → a walkable horizontal **platform**
   - its **left edge** and **right edge** → climbable vertical **ladders**
3. The pet can **walk** left/right on any platform.
4. The pet can **climb** up/down any ladder.
5. The user defines a **jump height**. From a platform, the pet can **jump** to a
   nearby ladder if that ladder is within jump-height reach, grab it, climb to the
   window's top edge, and walk there.
6. The world is **dynamic**: windows move, resize, open, minimize, and close while
   the pet is active. The pet must react gracefully (e.g. if the ladder it is
   climbing disappears, it falls).

---

## 2. Tech stack (decided)

| Concern | Choice | Why |
|---|---|---|
| Language | **C# / .NET 10** (LTS, supported to Nov 2028) | Fast to write, fast at runtime, first-class native interop, cross-platform |
| UI / rendering | **Avalonia UI** | Mature cross-platform .NET UI; supports transparent, borderless, topmost windows; one codebase for Windows + macOS |
| Native Windows APIs | **CsWin32** source generator (`Microsoft.Windows.CsWin32`) | Generates P/Invoke bindings from a text list; no hand-written signatures |
| Config | Plain `settings.json` (System.Text.Json) | Simple, user-editable |

> Before scaffolding, confirm Avalonia publishes a `net10.0`-compatible release and
> target `net10.0` in the csproj. If a specific Avalonia package lags, pin the
> newest version that supports `net10.0`.

---

## 3. Architecture

The key design principle: **isolate everything OS-specific behind one interface.**
The collision world is just "a set of rectangles" (windows + taskbar). Only the
code that *produces* those rectangles is platform-specific. Physics, the state
machine, geometry math, and rendering are 100% shared and unit-testable.

```
MaplePet/
  MaplePet.csproj
  app.manifest                  # per-monitor DPI awareness v2
  Program.cs
  App.axaml / App.axaml.cs

  Views/
    PetWindow.axaml             # the transparent full-screen overlay
    PetWindow.axaml.cs          # owns the canvas; hosts the game loop

  Engine/                       # SHARED, platform-agnostic, testable
    GameLoop.cs                 # tick scheduler
    WorldModel.cs               # rects -> platforms + ladders
    PetController.cs            # the state machine
    Physics.cs                  # gravity, movement integration
    Types.cs                    # Vec2, Rect, Platform, Ladder, WorldGeometry

  Platform/
    IWindowTracker.cs           # the one seam between shared + native code
    Windows/
      WindowsWindowTracker.cs
      NativeMethods.txt         # CsWin32 input (functions + constants to generate)
    MacOS/                      # FUTURE — leave a stub
      MacWindowTracker.cs

  Rendering/
    PetRenderer.cs              # sprite-sheet frame selection + draw
    DebugOverlay.cs             # draws detected rects/platforms/ladders

  Assets/
    pet-spritesheet.png

  settings.json
```

### 3.1 The platform seam

```csharp
// Platform/IWindowTracker.cs
public interface IWindowTracker
{
    // Returns the current visible window rectangles and the taskbar rectangle,
    // all in PHYSICAL screen pixels. Called a few times per second, not every frame.
    WorldGeometry Capture();
}

// Engine/Types.cs
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Top    => Y;
    public double Left   => X;
    public double Right  => X + Width;
    public double Bottom => Y + Height;
}

public sealed record WorldGeometry(
    IReadOnlyList<Rect> Windows,
    Rect Taskbar,
    TaskbarEdge TaskbarEdge);   // Bottom (default), Top, Left, Right

public enum TaskbarEdge { Bottom, Top, Left, Right }
```

`WindowsWindowTracker` is the only thing that touches Win32. A future
`MacWindowTracker` implements the same interface; nothing else changes.

### 3.2 Building the world from rectangles (shared)

```csharp
// Engine/WorldModel.cs
public readonly record struct Platform(double Y, double XStart, double XEnd); // walkable, horizontal
public readonly record struct Ladder(double X, double YTop, double YBottom);  // climbable, vertical

public sealed record World(IReadOnlyList<Platform> Platforms, IReadOnlyList<Ladder> Ladders);

public static class WorldModel
{
    public static World Build(WorldGeometry g)
    {
        // Base platform = the walkable face of the taskbar (top edge if docked bottom).
        // For each window rect:
        //   Platform(Top, Left, Right)
        //   Ladder(Left,  Top, Bottom)
        //   Ladder(Right, Top, Bottom)
        // Return combined lists.
    }
}
```

Keep this pure (no OS calls) so it can be unit-tested with synthetic rectangles.

### 3.3 Pet state machine (shared)

States and transitions map directly onto the requirements:

```
            ┌────────────── falls off edge / platform vanishes ──────────────┐
            v                                                                 │
   ┌────────────────┐   reach ladder within jumpHeight    ┌──────────────┐   │
   │    WALKING      │ ───────────── JUMP ───────────────> │   JUMPING    │   │
   │ (on a platform) │                                     │ (ballistic)  │   │
   └────────────────┘ <──── land on a platform top ─────── └──────────────┘   │
        │   ^                                                   │              │
        │   │ reach top of ladder -> step onto that window top  │ grab ladder  │
        │   │                                                    v              │
   grab │   └──────────────────────────── ┌──────────────┐ <────┘              │
ladder  └────────────────────────────────>│   CLIMBING   │                     │
                                           │ (on a ladder)│ ────────────────────┘
                                           └──────────────┘   ladder removed -> FALL
                                                  │
                                       reach bottom -> WALKING (taskbar) or FALLING

   FALLING: gravity only, no input, until it lands on any platform top.
```

- **Walking**: rests on a `Platform`; moves horizontally at `walkSpeed`. At a
  platform end it picks an action: turn around, attempt a jump to a nearby ladder,
  or walk off into Falling. (Start simple: turn around; add jump-seeking later.)
- **Jumping**: launched with vertical impulse derived from `jumpHeight` plus
  current horizontal velocity; gravity applied each tick. Resolves when it either
  lands on a platform top or its bounding box reaches a ladder line within reach →
  Climbing.
- **Climbing**: x is locked to the ladder's `X`; moves vertically at `climbSpeed`
  between `YTop` and `YBottom`. At the top, steps onto that window's top platform
  (Walking). At the bottom, lands on whatever platform is below or Falling.
- **Falling**: gravity only until landing.

**Dynamic-world rule (every state):** each tick, re-resolve the segment the pet is
attached to against the freshly built `World`. If the platform/ladder the pet is
standing on or climbing no longer exists (window closed/minimized/moved away),
transition to **Falling**.

`jumpHeight` semantics: the maximum vertical distance (in logical px) the pet can
gain in a jump. Use it both to (a) compute the launch impulse given `gravity`, and
(b) decide whether a ladder segment is "catchable" from the current platform.

### 3.4 Game loop & polling cadence (shared + view)

Avalonia has no built-in game loop. Use a `DispatcherTimer`:

- **Render/physics tick:** ~60 Hz (16 ms). Updates pet state and invalidates the
  canvas.
- **World poll:** ~5–10 Hz. `EnumWindows` is comparatively expensive — do **not**
  call `IWindowTracker.Capture()` every frame. Cache the last `World` and rebuild
  it only on the slower cadence. Physics reads the cached `World`.

---

## 4. The overlay window (Avalonia + Win32)

`PetWindow` must be:

- **Borderless:** `SystemDecorations="None"`
- **Transparent:** `Background="Transparent"`, `TransparencyLevelHint="Transparent"`
- **Always on top:** `Topmost="True"`
- **Not in the taskbar:** `ShowInTaskbar="False"`
- **Full virtual-screen sized & positioned** so its coordinate space maps cleanly
  to screen coordinates (mind multi-monitor; the virtual screen origin can be
  negative when a monitor sits left of / above the primary).

**Click-through** requires Win32 extended styles — Avalonia alone won't pass clicks
to the apps behind the pet. After the window opens, get the `HWND` via
`TryGetPlatformHandle()`, then apply `WS_EX_TRANSPARENT | WS_EX_LAYERED` to
`GWL_EXSTYLE` with `GetWindowLongPtr`/`SetWindowLongPtr`.

> ⚠️ Full click-through means the *pet itself* can't be clicked either. For
> drag-to-move / interaction (a later milestone), use the toggle approach: on a
> low-frequency timer, hit-test the cursor against the pet's bounding box; when the
> cursor is over the pet, clear `WS_EX_TRANSPARENT` so it's interactive, otherwise
> set it. Don't try per-pixel hit testing for v1.

---

## 5. Windows native layer (CsWin32)

List the APIs to generate in `Platform/Windows/NativeMethods.txt` (one symbol per
line). CsWin32 generates the bindings at build time.

```
# Window enumeration & geometry
EnumWindows
IsWindowVisible
IsIconic
GetWindowRect
GetWindowLongPtr
SetWindowLongPtr
FindWindow

# Correct visible bounds & cloaked-window filtering (DWM)
DwmGetWindowAttribute
DWMWINDOWATTRIBUTE          # need DWMWA_EXTENDED_FRAME_BOUNDS, DWMWA_CLOAKED

# Taskbar position & docked edge
SHAppBarMessage
APPBARDATA
ABM_GETTASKBARPOS

# Ex-style constants for click-through
WINDOW_EX_STYLE            # WS_EX_TRANSPARENT, WS_EX_LAYERED
WINDOW_LONG_PTR_INDEX      # GWL_EXSTYLE
```

### `WindowsWindowTracker.Capture()` algorithm

1. `EnumWindows`. For each `HWND`, keep it only if **all** hold:
   - `IsWindowVisible` is true
   - `IsIconic` is false (skip minimized)
   - it is **not cloaked**: `DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, …)` returns 0
     (filters hidden UWP/virtual-desktop windows)
   - it has a non-empty title and reasonable size (filters tool/ghost windows)
2. For each kept window, get its **true visible** rectangle via
   `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, …)`, **not**
   `GetWindowRect` — `GetWindowRect` includes the invisible drop-shadow border on
   Win10/11, which would float the pet a few px off the visible edge.
3. Taskbar: `SHAppBarMessage(ABM_GETTASKBARPOS, …)` → rectangle + docked edge.
   (Fallback: `FindWindow("Shell_TrayWnd", null)` + `GetWindowRect`.) Respect the
   docked edge so the base platform is the taskbar's *inner* face.
4. Return everything in **physical pixels** as `WorldGeometry`.

---

## 6. Coordinate space & DPI (read this — it's the #1 source of bugs)

`GetWindowRect` / DWM bounds are in **physical pixels**. Avalonia draws in
**logical (device-independent) pixels** scaled by `RenderScaling`. If you mix them,
the pet renders offset from where windows actually are, and the error grows with
the display scale (e.g. 150% / 200%).

Rules:

1. Ship an `app.manifest` declaring **per-monitor DPI aware v2**
   (`PerMonitorV2`). Reference it from the csproj
   (`<ApplicationManifest>app.manifest</ApplicationManifest>`).
2. Pick **one** world coordinate space and convert at the boundary only.
   Recommended: keep the world model in **logical px**. Convert the physical-pixel
   rectangles from the tracker into logical px (divide by the relevant scale
   factor) immediately inside / just after `Capture()`, before they enter
   `WorldModel.Build`.
3. For **v1, assume a single monitor or uniform scaling** across monitors and use
   the primary `RenderScaling`. Note mixed-DPI multi-monitor as a known limitation
   to revisit; per-monitor conversion is fiddly and not needed to prove the concept.

---

## 7. Configuration (`settings.json`)

```json
{
  "jumpHeight": 120,        // max vertical reach of a jump, logical px
  "walkSpeed": 90,          // px / second
  "climbSpeed": 70,         // px / second
  "gravity": 900,           // px / second^2
  "targetFps": 60,
  "worldPollHz": 8,         // how often window geometry is re-read
  "spriteSheet": "Assets/pet-spritesheet.png"
}
```

Load on startup; tolerate a missing file by writing defaults.

---

## 8. Build milestones (do them in order)

Each milestone should compile, run, and be visually verifiable before moving on.

- **M0 — Scaffold.** `dotnet new avalonia.app`, target `net10.0`, run a blank
  borderless, transparent, topmost, full-screen window covering the desktop. Verify
  you can see through it.

- **M1 — Click-through.** Apply `WS_EX_TRANSPARENT | WS_EX_LAYERED`. Verify you can
  click icons/apps *behind* the overlay.

- **M2 — Window tracker + debug overlay.** ⭐ *Most important early step.*
  Implement `WindowsWindowTracker`. Draw every detected window rectangle and the
  taskbar as colored outlines, and draw the derived **platforms** (top edges) and
  **ladders** (side edges) in distinct colors. Move and resize real windows and
  confirm the outlines track them correctly, snugly on the *visible* edges, with
  cloaked/minimized windows excluded. **Get this rock-solid before adding the pet.**

- **M3 — Game loop + static pet.** Add the `DispatcherTimer` loop. Draw a sprite
  resting on the taskbar. Decouple the world-poll cadence from the render cadence.

- **M4 — Walking + gravity.** Pet walks left/right on the taskbar and turns around
  at screen edges. Gravity so it rests on the platform.

- **M5 — Climbing.** When the pet reaches a ladder, it grabs and climbs up/down,
  stepping onto the window's top platform at the top.

- **M6 — Jumping with `jumpHeight`.** From a platform, the pet jumps to a ladder
  within `jumpHeight` reach, grabs it, and climbs. Wire jump-seeking into the
  Walking edge decision.

- **M7 — Full state machine + dynamic world.** Integrate all states. Enforce the
  dynamic-world rule: if the pet's current platform/ladder disappears, it falls and
  recovers on the next surface.

- **M8 — Config.** Read `settings.json` for `jumpHeight`, speeds, gravity, FPS.

- **M9 — Polish.** Sprite-sheet animation frames per state (walk/climb/jump/idle),
  occasional idle behaviors, and drag-to-move via the click-through toggle from §4.

---

## 9. Known gotchas (recap)

1. **DPI / coordinate space** — §6. The big one. Establish one space up front.
2. **Drop-shadow borders** — use `DWMWA_EXTENDED_FRAME_BOUNDS`, not `GetWindowRect`,
   for the visible edge.
3. **Cloaked windows** — filter with `DWMWA_CLOAKED` or you'll get phantom
   platforms from hidden UWP windows.
4. **Taskbar can dock to any edge** and may auto-hide — read position via
   `SHAppBarMessage` and re-poll so the base platform stays correct.
5. **Polling cost** — never call `EnumWindows` at 60 Hz; poll at `worldPollHz`.
6. **Click-through hides the pet from the mouse** — toggle the ex-style when the
   cursor is over the pet (§4).
7. **Negative coordinates** — multi-monitor virtual-screen origins can be negative;
   the world must handle them.
8. **Disappearing surfaces** — a window can close mid-climb; the pet must fall
   gracefully (the §3.3 dynamic-world rule).

---

## 10. Future: macOS port

Only `Platform/MacOS/MacWindowTracker.cs` needs writing; everything in `Engine/`,
`Rendering/`, and `Views/` is shared.

- **Window list & bounds:** `CGWindowListCopyWindowInfo` (Quartz / Core Graphics).
- **Base platforms:** there is no taskbar — use the **menu bar** (top) and/or the
  **Dock** as base platforms; map them into the same `WorldGeometry`.
- **Click-through:** `NSWindow.ignoresMouseEvents = true`.
- **Permissions:** reading other apps' window info on recent macOS requires the
  **Screen Recording** permission; surface a clear prompt if it's missing.
- Keep all geometry in logical points and platform-agnostic so the shared engine is
  reused verbatim.

---

## 11. First commands

```bash
cd MaplePet
dotnet new install Avalonia.Templates
dotnet new avalonia.app
# set <TargetFramework>net10.0</TargetFramework> in MaplePet.csproj
# add <ApplicationManifest>app.manifest</ApplicationManifest> in MaplePet.csproj
dotnet add package Microsoft.Windows.CsWin32
dotnet run
```

Then implement **M0 → M9** in order. Prioritize **M2** (the debug overlay): being
able to *see* the platforms and ladders the tracker produces, and watch them track
real windows, is what makes the rest of the project tractable.
