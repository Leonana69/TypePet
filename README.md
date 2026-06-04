# MaplePet

**Version 1.0.0** · see [`CHANGELOG.md`](CHANGELOG.md)

A desktop pet for Windows that lives on a transparent, click-through, always-on-top
overlay and walks along your taskbar. The "level geometry" (platforms + ladders) is
derived in real time from the windows currently open on your desktop.

See [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) for the full design and milestones.

## Status

| Area | What works |
|---|---|
| Overlay | Transparent, borderless, topmost, click-through, full-virtual-screen (`WS_EX_TRANSPARENT \| WS_EX_LAYERED`) |
| Window tracker | Detects visible windows (DWM visible bounds; cloaked/minimized/tool-window filtered) + taskbar |
| Visible-only geometry | Window top/side edges are clipped against windows **in front** (Z-order) and the taskbar, so an occluded edge yields only its visible segments |
| Debug overlay | Draws windows, **platforms** (green), and **ladders** (orange) — toggle with `showOverlay` |
| Roaming | Treats the visible platforms/ladders as a navigation graph (clipped to the visible screen): picks a random point on a reachable window top edge, plans a path, and follows it — walking, **jumping up** onto platforms within `jumpHeight`, climbing ladders only for taller gaps, and **down-jumping** through to the platform below. How often it wanders is set by `roamingLevel` |
| Jumps | Jumps and down-jumps follow a real **parabolic arc** under gravity (state JUMP); to mount a ladder the pet runs up and jumps early so the arc's **peak meets the ladder line**, then grabs on mid-air |
| States | STAND (idle) / WALK / ROPE (climbing) / JUMP (airborne); idles between trips |
| Drag | Grab the pet with the mouse (it follows the cursor in JUMP); release and it falls to the platform below and stands |
| Tray menu | A tray icon with **Settings…** (live-editable parameters) and **Exit** |
| Dynamic world | Rebuilds the graph and replans whenever windows move/open/close; if its surface vanishes it falls and recovers |
| Fullscreen hide | Hides the overlay while a borderless/exclusive fullscreen app is foreground (taskbar-covering window, not a mere maximize); returns on alt-tab back to the desktop |

The pet is a MapleStory character (Body + Head + equipped items, including item effects, rendered
from `Assets/footage`) that stands, walks, jumps, and climbs with per-state poses. If the footage
fails to load it falls back to a state-tinted rectangle.

Not yet implemented: attack animations (the footage already carries swing/stab/shoot poses to wire up).

## Requirements

- Windows 10/11, x64
- .NET 10 SDK

## Run

```powershell
dotnet run --project MaplePet.csproj
```

You'll see your detected windows outlined, the derived platforms/ladders drawn on top,
and the MapleStory character (the pet) roaming them — walking, jumping up onto nearby ledges, climbing
window edges for taller gaps, and down-jumping between them. The overlay is click-through,
so everything behind it stays usable; you can
still grab and drag the pet with the mouse. Quit from the tray icon's **Exit** (or Ctrl+C
in its console).

Smoke test (auto-closes after N seconds, useful for CI):

```powershell
dotnet run --project MaplePet.csproj -- --smoke 3
```

## Configuration

`settings.json` (auto-created with defaults; out-of-range / NaN values fall back to defaults):

| Key | Default | Meaning |
|---|---|---|
| `jumpHeight` | `150` | Max vertical reach (px) to jump straight up onto a higher platform (taller gaps need a ladder) |
| `roamingLevel` | `50` | 0–100 restlessness: chance it wanders to a new spot when idle (0 = stay put, 100 = always roam; eased so low values stay put far more than linear) |
| `walkSpeed` | `90` | px / second |
| `climbSpeed` | `70` | px / second |
| `gravity` | `900` | px / second² |
| `targetFps` | `60` | render/physics tick rate |
| `worldPollHz` | `8` | how often window geometry is re-read |
| `showOverlay` | `false` | Draw the debug window-edge / path overlay |
| `hideWhenFullscreen` | `true` | Hide the pet while a borderless/exclusive fullscreen app (game/video) is foreground; a normal maximized window keeps it visible |

Lower `roamingLevel` to make the pet calmer (it wanders less often); raise `jumpHeight` to let it
reach windows that float higher above the taskbar. Edit these live via the tray **Settings…** dialog.

## Replacing the placeholder pet

The pet is drawn in [`Rendering/PetRenderer.cs`](Rendering/PetRenderer.cs) (`PetRenderer.Draw`).
Swap the rectangle for an image / sprite-sheet frame there; the controller already exposes
`Pos`, `Size`, and `Facing`. Drop sprite assets in `Assets/`.

## Architecture

Everything OS-specific sits behind `IWindowTracker` (`Platform/Windows/WindowsWindowTracker.cs`,
via CsWin32). The `Engine/` code (types, world model, physics, state machine, screen-space
conversion) is pure and portable — a future macOS port only needs a new tracker
(`Platform/MacOS/`).
