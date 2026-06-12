# Changelog

All notable changes to TypePet are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version lives in [`TypePet.csproj`](TypePet.csproj) (`<Version>`); bump it together with the
matching `AssemblyVersion`/`FileVersion` and add an entry here when releasing.

## [Unreleased]

### Added
- **macOS support.** The project now multi-targets `net10.0-windows` and `net10.0`, with every
  OS-specific concern behind a `Platform/Abstractions/` interface set resolved by a `PlatformServices`
  factory (`Engine`/`Rendering`/`Api`/`Views` are shared, with no `OperatingSystem.IsWindows()`
  branches). The macOS layer (`Platform/Mac/`) uses CoreGraphics / AppKit (`objc_msgSend`) / Carbon via
  plain P/Invoke: window enumeration through `CGWindowListCopyWindowInfo`, an `NSWindow` overlay floated
  across all Spaces, a permission-free click-through grab (toggled `ignoresMouseEvents` + native pointer
  events), a Carbon `RegisterEventHotKey` say-bar hotkey, a file-backed single-instance guard, and
  `SMAppService` start-at-login — needing **no TCC permissions**. Ships with an ad-hoc-signed `.app`
  bundle script (`packaging/macos/`) and a GitHub Actions matrix building both heads.

### Changed
- Tray menu icons are now drawn as cross-platform vector glyphs (no dependency on the Windows-only
  Segoe Fluent / MDL2 icon fonts), so they look identical on Windows and macOS.

### Added (earlier)
- Window **bottom edges** are now walkable platforms, not just the top edge. The pet can roam onto a
  window's lower border, drop onto it from the roof above, and climb back up the side edges — each
  bottom edge is clipped against windows in front and the taskbar just like the top edge.
- Auto-hide: the pet tucks away while a borderless or exclusive-fullscreen app (a game or video) is
  the foreground window, and returns when you alt-tab back to the desktop. A window that's merely
  maximized keeps the pet visible. Toggle it in Settings → System ("Hide in fullscreen apps") or via
  `hideWhenFullscreen` in `settings.json` (default on).

## [0.1.0] - 2026-06-03

First release. The desktop pet walks, jumps, and climbs along the geometry derived from your
open windows and the taskbar.

### Added
- Transparent, click-through, always-on-top overlay spanning the full virtual screen.
- Window tracker that derives platforms and ladders from the visible (Z-order-clipped) window edges.
- Roaming with path-planning: walking, jumping up, climbing ladders, and down-jumping, with a
  parabolic jump arc under gravity.
- Character sprite rendering (Body + Head + equipped items) with per-state poses, plus drag.
- A user-selectable character library: pick, import/export (zip), live-swap, and persistence.
- Frosted-glass config UI with Characters and Settings tabs, and a tray icon with live settings.
- "Start with Windows" support and a program icon (exe, taskbar, tray, title bar).
