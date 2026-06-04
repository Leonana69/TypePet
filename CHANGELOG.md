# Changelog

All notable changes to MaplePet are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version lives in [`MaplePet.csproj`](MaplePet.csproj) (`<Version>`); bump it together with the
matching `AssemblyVersion`/`FileVersion` and add an entry here when releasing.

## [Unreleased]

### Added
- Window **bottom edges** are now walkable platforms, not just the top edge. The pet can roam onto a
  window's lower border, drop onto it from the roof above, and climb back up the side edges — each
  bottom edge is clipped against windows in front and the taskbar just like the top edge.
- Auto-hide: the pet tucks away while a borderless or exclusive-fullscreen app (a game or video) is
  the foreground window, and returns when you alt-tab back to the desktop. A window that's merely
  maximized keeps the pet visible. Toggle it in Settings → System ("Hide in fullscreen apps") or via
  `hideWhenFullscreen` in `settings.json` (default on).

## [1.0.0] - 2026-06-03

First tagged release. The desktop pet walks, jumps, and climbs along the geometry derived from your
open windows and the taskbar.

### Added
- Transparent, click-through, always-on-top overlay spanning the full virtual screen.
- Window tracker that derives platforms and ladders from the visible (Z-order-clipped) window edges.
- Roaming with path-planning: walking, jumping up, climbing ladders, and down-jumping, with a
  parabolic jump arc under gravity.
- MapleStory character rendering (Body + Head + equipped items) with per-state poses, plus drag.
- A user-selectable character library: pick, import/export (zip), live-swap, and persistence.
- Frosted-glass config UI with Characters and Settings tabs, and a tray icon with live settings.
- "Start with Windows" support and a program icon (exe, taskbar, tray, title bar).
