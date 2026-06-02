# MaplePet

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
| Debug overlay | Draws windows, **platforms** (green), and **ladders** (orange) |
| Walking | Pet walks platforms, steps across abutting same-height windows, turns at solid ends |
| Climbing (roaming) | Passing a reachable ladder, it climbs with probability `roamingChance`; reachability bounded by `jumpHeight` |
| Dropping | Comes down by climbing a ladder **or** walking off a cliff edge and falling |
| Dynamic world | Re-resolves its surface every tick; if the platform/ladder vanishes, it falls |

The pet is tinted by state: **blue** walking, **green** climbing, **orange** falling.

Not yet implemented: a true ballistic jump arc, sprite animation (M9), and drag-to-move.

## Requirements

- Windows 10/11, x64
- .NET 10 SDK

## Run

```powershell
dotnet run --project MaplePet.csproj
```

You'll see your detected windows outlined, the derived platforms/ladders drawn on top,
and a blue rectangle (the pet) walking back and forth along the taskbar. The overlay is
click-through, so everything behind it stays fully usable. Close it from its console
(Ctrl+C) or end the `MaplePet` process.

Smoke test (auto-closes after N seconds, useful for CI):

```powershell
dotnet run --project MaplePet.csproj -- --smoke 3
```

## Configuration

`settings.json` (auto-created with defaults; out-of-range / NaN values fall back to defaults):

| Key | Default | Meaning |
|---|---|---|
| `jumpHeight` | `50` | Max vertical reach (px) to grab a ladder from a platform |
| `roamingChance` | `35` | 0–100% chance to climb a reachable ladder it passes |
| `walkSpeed` | `90` | px / second |
| `climbSpeed` | `70` | px / second |
| `gravity` | `900` | px / second² |
| `targetFps` | `60` | render/physics tick rate |
| `worldPollHz` | `8` | how often window geometry is re-read |

Raise `roamingChance` toward 100 to make the pet climb almost everything it passes; raise
`jumpHeight` to let it reach windows that float higher above the taskbar.

## Replacing the placeholder pet

The pet is drawn in [`Rendering/PetRenderer.cs`](Rendering/PetRenderer.cs) (`PetRenderer.Draw`).
Swap the rectangle for an image / sprite-sheet frame there; the controller already exposes
`Pos`, `Size`, and `Facing`. Drop sprite assets in `Assets/`.

## Architecture

Everything OS-specific sits behind `IWindowTracker` (`Platform/Windows/WindowsWindowTracker.cs`,
via CsWin32). The `Engine/` code (types, world model, physics, state machine, screen-space
conversion) is pure and portable — a future macOS port only needs a new tracker
(`Platform/MacOS/`).
