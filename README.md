# TypePet

**Version 1.0.0** · see [`CHANGELOG.md`](CHANGELOG.md)

A desktop pet for **Windows and macOS** that lives on a transparent, click-through, always-on-top
overlay and walks along your taskbar (on macOS: the bottom of the screen / the Dock). The "level
geometry" it roams — platforms and ladders — is derived in real time from the windows currently open on
your desktop, so the pet climbs, jumps, and follows you across monitors as you work.

It has grown well past a simple overlay into a small desktop companion: a floating **say bar** backed by
an in-app **LLM chatbot** (Anthropic Claude + any OpenAI-compatible endpoint) with keyless web search and
a game-knowledge lookup; a hot-reloaded library of **user commands** with sandboxed JavaScript; a
community **command hub** with one-click GitHub publishing; an optional local **MCP server** that lets
external agents drive the pet; persisted **reminders**; and an importable **character** library.

Built on **.NET 10** and **Avalonia 11.3**.

---

## Features

### Desktop pet & overlay
- **Transparent, borderless, click-through, always-on-top overlay** that covers exactly one display at a
  time and follows the pet across monitors by moving/resizing (it is *not* a single full-virtual-screen
  window). Everything behind it stays clickable.
- **Real-time level geometry.** Each open window's top/bottom edges become walkable platforms and its
  left/right edges become climbable ladders — emitting only the *visible* segments (clipped against
  windows in front by Z-order, and against the taskbar/Dock and the display bounds). The world rebuilds
  automatically as you open, move, resize, or close windows.
- **Roaming** over a path-planned navigation graph when idle; how often it wanders is set by
  `roamingLevel`. Six movement kinds are chosen automatically by the planner: **walk**, **climb up**
  (ladders are up-only), **jump up** onto a higher overlapping platform within `jumpHeight`, **drop
  down** through the current platform, **edge-drop** off a cliff into free-fall, and **gap-jump** — a
  lateral ballistic leap across a gap, validated by a per-frame physics replay.
- **Real parabolic physics** under gravity for every jump, drop, and fall; to mount a ladder the pet
  launches early so the arc's apex meets the ladder line, then grabs on mid-air.
- **Five states:** STAND, WALK, ROPE (climbing), JUMP (airborne or dragged), and FLY (a gravity-free
  vertical glide, used only by the `fly` control action).
- **Layered sprite rendering.** The pet is a full character — Body + Head + every equipped item
  (hair, cap, face, accessories, weapon, shield, item effects) — drawn back-to-front from the
  `Assets/footage` manifests, pinned at the navel and mirrored when facing right. If footage fails to
  load it falls back to a state-tinted rectangle so the overlay still shows something.
- **Attack animations & expressions.** A random character-specific strike pose (stab/swing/shoot) plus
  swappable face expressions, driven by the control API, the chatbot, or MCP.
- **Drag** the pet with the mouse (the hit-box matches the drawn sprite): it flails after the cursor,
  falls to the platform below on release, and switches display if dragged to another monitor.
- **Dynamic replanning & rescue:** geometry is re-polled at `worldPollHz`, and the pet replans only on a
  real change (a focus-only Z-order reshuffle causes no stutter); if its surface vanishes it falls and
  recovers.
- **Fullscreen-hide:** the overlay tucks away (and the loops freeze) while a borderless/exclusive
  fullscreen app — a game or video — is foreground, and returns when you alt-tab back to the desktop. A
  merely maximized window keeps the pet visible. Toggle with `hideWhenFullscreen`.
- **Debug overlay** (`showOverlay`) draws the tracked windows, taskbar, derived platforms (green) and
  ladders (orange), and the planned path with its sampled jump arcs.

### AI chat
- **Say bar** — a frosted, borderless input near the bottom of the pet's display. Open it by
  **double-clicking the pet** or pressing the **global hotkey** (default `Ctrl+Alt+Space`); Esc or a
  click away dismisses it. The same bar is both the plain "speak" input and the chat input.
- **Chatbot toggle.** With the chatbot **off**, the pet simply repeats what you type. With it **on**
  (`enableChatbot`), the bar routes to your LLM provider, the pet "thinks", then speaks the reply.
- **Two backends, many providers.** `Claude` uses the official Anthropic SDK; every other provider uses
  the OpenAI-compatible backend, so **OpenAI, DeepSeek, Ollama, LM Studio, vLLM** and friends differ only
  by base URL + model + key. Switch providers in **Settings → Chatbot**; it applies on the next message,
  no restart.
- **Pet-body tools.** During a reply the model can emote and move the pet (`face`, `walk_to`, `move_to`,
  `do_action`, `set_expression`, …). There is deliberately no "say" tool — the app speaks the model's
  final text for you.
- **Keyless web access.** Built-in `web_search` (DuckDuckGo) and `web_fetch` (readable page text) tools
  the model calls on its own; toggle with `enableWebSearch`.
- **Game-knowledge lookup (RAG).** A `maple_lookup` tool answers MapleStory questions from the bundled
  `Assets/Program/Knowledge/*.json` sources (language-prioritized); always on when the chatbot is.
- **Chat history panel** with markdown, clickable links, source citations, and inline images — toggle
  with the ⌄/⌃ button.

### Commands & scripting
- **Slash commands.** Type `/` in the say bar for an autocomplete dropdown. Commands run locally and
  deterministically with **no LLM**, so they work even with the chatbot off. Three built-ins always win
  name collisions: **`/remind`**, **`/clear`**, **`/help`**.
- **Declarative kinds (no code):** `text` (speak a line), `clipboard` (copy + confirm), `image` (show a
  bundled/remote image), `link` (a clickable button), `prompt` (send a body to the LLM as a system
  prompt), and `pet` (a short choreography of moves/emotes). Argument tokens `{{args}}`, `{{1}}`, `{{2}}`
  are substituted.
- **`kind:script` — sandboxed JavaScript** (via Jint): no filesystem, CLR, or process access. Scripts
  get globals like `args`, `say`, `clipboard`, `image`, `link`, `hold`, `expression`, `action`, `walk`,
  `face`, and `rank()`, under statement/memory/wall-clock limits. Gated globally by `enableUserScripts`
  (default on) and compiled in via the `TYPEPET_SCRIPTING` build constant.
- **Per-command network grants.** A script can reach the network only if it declares `hosts: a.com,
  b.com` **and** you approve that exact allowlist in the Commands tab. Requests are HTTPS-only with
  SSRF/DNS-rebind protection and strict budgets; editing the `hosts:` line revokes the grant.
- **Authoring & hot-reload.** Each command is a folder with a `command.md` (frontmatter + body) plus
  folder-local images; a file watcher hot-reloads adds/edits/renames/deletes with no restart. Manage them
  in the **Commands** tab (tray → *Commands…*): per-command on/off, delete, export to zip, or *Submit to
  hub…*. See [`Assets/Commands/command_helper.md`](Assets/Commands/command_helper.md) for the full
  authoring reference.
- **Ships with** `/roll` (a dice roller) and `/wave`. More commands — `/rank`, `/sf`, `/dailyboss`,
  `/fortune`, … — are installed from the hub, not bundled.

### Command hub
- **Browse & install** (tray → *Browse hub…*). Anonymously fetches the registry from
  [`Leonana69/TypePet-Commands`](https://github.com/Leonana69/TypePet-Commands) and lists Official /
  Community commands with **Install / Update / Remove**. A pre-install disclosure summarizes the author,
  kind, and any network hosts.
- **Integrity & safety.** Installs are SHA-256-verified and size-capped over HTTPS; a freshly installed
  `kind:script` command lands **disabled and unapproved**, so it can never run — or touch the network —
  until you opt in. Updates follow SemVer and preserve your enable/approval choices when safe.
- **One-click publish.** *Submit to hub…* uses a GitHub device-flow login (token stored in the OS secret
  store), forks the repo, commits your command, and opens a pull request. With no OAuth client wired into
  the build it falls back to exporting a zip and opening the repo.

### MCP server
- An optional **local Model Context Protocol server** (Streamable HTTP on `127.0.0.1`) exposes the pet's
  control surface so an MCP-capable LLM or agent can drive it. Off by default — enable with
  `enableMcpServer` and set `mcpPort` (default `8765`); takes effect on next launch.
- Exposes the pet as discrete tools (`capabilities`, `describe`, `do_action`, `move_to`, `walk_to`,
  `face`, `set_expression`, `say`, …). Call `capabilities` first for the current character's vocabulary.

### Reminders & characters
- **`/remind`** schedules one-off (`10m`, `1h30m`, `14:23`) or recurring (`-d` daily, `-w` weekly, `-m`
  monthly) reminders, plus `/remind list | cancel <id> | clear`. They persist across restarts; when due,
  the pet freezes an alarm-clock bubble and plays an action even with the bar closed. With the chatbot
  on, you can also ask for reminders in natural language.
- **Character library** (config window → **Characters** tab). A card per character — the built-in
  *Default* plus any you import — with **Import .zip**, and right-click **Rename / Export / Delete** on
  imported ones. Click a card to wear it and live-swap the pet; the choice persists in
  `currentCharacterId`.

---

## Requirements

- Windows 10/11 (**x64**) **or** macOS **13+** (Apple Silicon or Intel)
- **.NET 10 SDK**

The project multi-targets `net10.0-windows` (the Win32 layer, via CsWin32 — pinned to x64) and `net10.0`
(the portable head that runs on macOS). Because it multi-targets, `dotnet build`/`run`/`publish` need a
`-f` to pick the head for your OS. `Directory.Build.props` sets `EnableWindowsTargeting=true` so the
Windows head also *restores/builds* on a non-Windows host (it just can't *run* there).

## Build & run

**Windows:**
```powershell
dotnet run --project TypePet.csproj -f net10.0-windows
```

**macOS:**
```bash
dotnet run --project TypePet.csproj -f net10.0
# or build a double-clickable, ad-hoc-signed app bundle (no Apple account needed):
./packaging/macos/build-macos-bundle.sh        # produces artifacts/TypePet.app
open artifacts/TypePet.app
```

You'll see your detected windows outlined, the derived platforms/ladders, and the sprite pet roaming
them. The overlay is click-through, so everything behind it stays usable — you can still grab and drag
the pet. Open the say bar with a double-click (or `Ctrl+Alt+Space`); quit from the tray icon's **Exit**
(or `Ctrl+C` in its console).

Smoke test (auto-closes after N seconds, useful for CI):

```bash
dotnet run --project TypePet.csproj -f net10.0 -- --smoke 3      # (-f net10.0-windows on Windows)
```

`--smoke` is the only user-facing flag; `Program.cs` also defines several dev-only `--*-test` flags
(`--nav-test`, `--hub-test`, `--rag-test`, `--commands-test`, `--render-poses`, `--mac-windump`, …) that
run a single subsystem check headlessly and exit.

## Using the chatbot

The chatbot is **off by default**. To enable it:

1. Open **Settings → Chatbot** (tray → *Settings…*) and turn on **Enable chatbot**.
2. Pick or configure a provider. Two presets are seeded: **Claude** (Anthropic) and **OpenAI
   Compatible** (point its base URL + model at OpenAI, DeepSeek, a local Ollama/LM Studio, etc.).
3. Paste the provider's API key. **Keys are stored in the OS secret store** — Windows DPAPI or the macOS
   login Keychain — and **never written to `settings.json`.** A local provider like Ollama needs no key.

Then open the say bar and talk to the pet. Slash commands work regardless of this setting.

## Configuration

Settings live in `settings.json` (auto-created with defaults; out-of-range or `NaN` values fall back to
defaults). The most useful hand-tunable keys:

| Key | Default | Meaning |
|---|---|---|
| `jumpHeight` | `150` | Max vertical reach (px) to jump straight up onto a higher platform; taller gaps need a ladder. |
| `roamingLevel` | `50` | 0–100 restlessness when idle (quadratically eased: low values stay put far more than linear; 0 = never roam, 100 = always). |
| `walkSpeed` | `90` | Walk/run speed, px/second (also the launch speed for gap-jumps). |
| `climbSpeed` | `70` | Ladder climb speed, px/second. |
| `gravity` | `900` | px/second², for every arc and fall. |
| `targetFps` | `60` | Render/physics tick rate (clamped 1–240). |
| `worldPollHz` | `8` | How often window geometry is re-read (Hz). |
| `showOverlay` | `false` | Draw the debug window-edge / platform / ladder / path overlay. |
| `hideWhenFullscreen` | `true` | Hide the pet while a borderless/exclusive fullscreen app is foreground. |
| `currentCharacterId` | `"default"` | Which character (Characters tab) the pet wears. |
| `sayInputHotkey` | `"Ctrl+Alt+Space"` | Global chord that opens the say bar (needs a strong modifier; double-clicking the pet always works too). |
| `enableChatbot` | `false` | Route the say bar to the LLM provider instead of plain "say". |
| `enableWebSearch` | `true` | Enable the keyless `web_search` / `web_fetch` tools. |
| `enableUserScripts` | `true` | Global gate for `kind:script` (sandboxed JS) commands. |
| `enableMcpServer` | `false` | Expose the pet over the local MCP server (next launch). |
| `mcpPort` | `8765` | Localhost port for the MCP server (1–65535). |
| `chatHistoryVisible` | `true` | Show the say bar's conversation-history panel. |

The remaining keys are managed for you by the UI and not meant to be hand-edited: `providers` /
`activeProviderId` (LLM profiles), `reminders`, `disabledCommandIds`, and `networkApprovedCommands` (the
per-script network grants). Everything in the table above is also editable live from the tray
**Settings…** dialog.

> API and GitHub keys are **not** in `settings.json` — they live in the OS secret store (DPAPI / Keychain).

## Architecture

Every OS-specific concern sits behind a small set of interfaces in `Platform/Abstractions/`
(`IWindowTracker`, `IOverlayEffects`, `IPetInput`, `IGlobalHotkey`, `ISingleInstance`, `IStartupAtLogin`,
`IAppPaths`, `ITrayGlyphs`, `ISecretStore`, `IPlatformServices`), resolved by the
`Platform/PlatformServices` factory — the one place that names per-OS types. `Engine/` (types, world
model, nav graph, physics, the state machine, command store, settings), `Rendering/`, `Api/`, and
`Views/` are shared and contain no `OperatingSystem.IsWindows()` branches. The chatbot, the MCP server,
and the say bar's pet-body tools all drive the pet through the same in-process `IPetControl` facade.

- **Windows** (`Platform/Windows/`, compiled only under `net10.0-windows`): Win32 via CsWin32 — window
  enumeration (DWM bounds), a `WS_EX_TRANSPARENT | WS_EX_LAYERED` click-through overlay, a polled cursor
  + low-level mouse/keyboard hooks, a named-mutex single-instance guard, the HKCU Run key, and a DPAPI
  secret store.
- **macOS** (`Platform/Mac/`, compiled only under `net10.0`): plain P/Invoke to CoreGraphics /
  CoreFoundation / AppKit (`objc_msgSend`) and Carbon — window enumeration via
  `CGWindowListCopyWindowInfo` (no titles read, so no Screen Recording prompt), an `NSWindow` overlay
  floated across every Space at screen-saver level, a click-through grab that toggles `ignoresMouseEvents`
  while the cursor is over the pet, a Carbon `RegisterEventHotKey` hotkey, a file-backed single-instance
  guard, `SMAppService` start-at-login, and a Keychain-backed secret store. The whole macOS feature set
  needs **no TCC permissions**.

CI (`.github/workflows/build.yml`) builds each head on its native runner and uploads the Windows exe and
the macOS `.app` bundle. The macOS bundle is published self-contained (not single-file — that breaks
CoreCLR startup inside a `.app`) and ad-hoc-signed; notarization is intentionally out of this
credential-free flow.

**Data & secrets.** Run from source, characters and commands live in the repo's `Assets/` tree; in a
packaged build they live under `%LOCALAPPDATA%\TypePet\` (Windows) or `~/Library/Application
Support/TypePet/` (macOS). API/GitHub keys live in the platform secret store, never on disk in plaintext.

## License

TypePet's **source code** is free software: you can redistribute it and/or modify it under the terms of
the **GNU General Public License, version 3 or (at your option) any later version** — see [`LICENSE`](LICENSE).
It is distributed WITHOUT ANY WARRANTY; see the license for details.

Copyright © 2026 leonana69 and contributors.
