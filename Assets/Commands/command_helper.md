# Writing a MaplePet command

This is a guide to authoring your own `/slash` commands for MaplePet. Commands are plain files — no
coding required for most of them — and they hot-reload, so you can edit and see the result without
restarting the app.

> This file (`command_helper.md`) is just documentation. It sits at the **root** of the commands folder,
> not inside a `cmd_*` sub-folder, so the app ignores it — only sub-folders that contain a `command.md`
> become commands.

---

## 1. TL;DR — your first command in 20 seconds

Create a folder with a single file:

```
Commands/
└── cmd_hello/
    └── command.md
```

`cmd_hello/command.md`:

```
---
name: hello
kind: text
usage: /hello [name]
help: Greet a Mapler.
---
Hi there, {{args}}! Welcome to Maple World ✨
```

Open the say bar, type `/hello Steve`, and the pet says *“Hi there, Steve! Welcome to Maple World ✨”*.
The command shows up the moment you save the file — no restart.

---

## 2. Where commands live & how they load

- Each command is a **folder** containing a `command.md` (plus any images it uses).
- The folder name is just an id — `cmd_hello`, `my-thing`, anything filesystem-safe. The word you actually
  type (`/hello`) comes from the `name:` field, **not** the folder name.
- All command folders live under the **commands root**:
  - Running from source: `&lt;repo&gt;/Assets/Commands`
  - Installed build: `%LOCALAPPDATA%\MaplePet\Commands` (Windows) /
    `~/Library/Application Support/MaplePet/Commands` (macOS)
  - The **Commands** tab (tray → “Commands…”) has an **Open folder** link that takes you there.
- **Hot-reload:** add, edit, rename, or delete a folder and the change applies within ~0.5 s.
- **Uploading / sharing:** a command is just a folder — zip it up and the recipient drops it into their
  commands folder. (Keep each command's images *inside* its own folder so they travel with it.)

---

## 3. Anatomy of `command.md`

A command file has two parts: a **frontmatter** block fenced by `---`, then the **body**.

```
---
key: value          ← frontmatter: the command's settings
key: value
---
The body.           ← what this means depends on the `kind`
```

The **body** is the *content* of the command:
- `kind: prompt` → the body is the prompt sent to the AI.
- `kind: script` → the body is the JavaScript to run.
- `kind: pet` → the body is a list of pet actions, one per line.
- `kind: text` → the body is what the pet says (unless you set `text:` in the frontmatter).
- Other kinds ignore the body.

---

## 4. Frontmatter keys

| Key | Applies to | Meaning |
|-----|-----------|---------|
| `name` | all (required) | The command word, no `/`. 1–32 chars: letters, digits, `-`, `_`. |
| `kind` | all (required) | One of: `text`, `clipboard`, `image`, `link`, `prompt`, `pet`, `script`. |
| `usage` | all | The hint shown in the dropdown, e.g. `/rank [server] <name>`. Defaults to `/name`. |
| `help` (or `description`) | all | One-line description shown in the dropdown and `/help`. |
| `aliases` | all | Other names that trigger it, comma-separated. e.g. `aliases: hi, hey` |
| `holdSeconds` | all | How long the pet holds the result bubble (and stays still). Default ~12s (images 120s). |
| `reaction` | all | A facial expression the pet wears afterward. `reaction: smile` or `reaction: smile\|6` (6 s). |
| `clipboard` (or `copy`) | clipboard | The text to copy. |
| `image` | image | A file in this folder (e.g. `guide.png`), or an `avares://`/`http(s)://` URL. |
| `text` (or `say`) | text | What the pet says (instead of the body). |
| `link` | link | `Title\|https://example.com` — or just a URL. |
| `roll` | prompt | A weighted random pick exposed as `{{roll}}` (see Prompt). |
| `requiresChat` | prompt | `false` to skip the “chatbot enabled” requirement (a provider/key is still needed). |

Values may be quoted (`"…"` or `'…'`) but usually don't need to be. Lines starting with `#` inside the
frontmatter are ignored.

---

## 5. Argument placeholders

Whatever the user types after the command name is the **arguments**. Use these tokens anywhere in the
body or in text-bearing frontmatter values:

| Token | Becomes |
|-------|---------|
| `{{args}}` | The full argument string. For `/hello a b c` → `a b c`. |
| `{{1}}`, `{{2}}`, … | The 1st, 2nd, … whitespace-separated word. Missing ones become empty. |

Prompt commands also get:

| Token | Becomes |
|-------|---------|
| `{{name}}` | The argument text, or `Mapler` if none was given. |
| `{{date}}` | Today's date, e.g. `Friday, June 6, 2026`. |
| `{{expressions}}` | The worn character's available facial expressions (comma-separated). |
| `{{roll}}` | A value chosen by weight from the `roll:` field (see below). |

Unknown `{{…}}` tokens are left as-is.

---

## 6. The kinds (with examples)

### `text` — the pet says something

```
---
name: motd
kind: text
usage: /motd
help: A little message of the day.
holdSeconds: 8
---
Remember to claim your daily gifts, {{name}}! 🎁
```

If you'd rather keep it on one line, drop the body and use `text:` in the frontmatter instead.

### `clipboard` — copy text to the clipboard

```
---
name: ssc
kind: clipboard
usage: /ssc
help: Copy "Sacred Symbol/claim" to the clipboard.
clipboard: Sacred Symbol/claim
---
```

The pet confirms with a “Copied … 📋” bubble and the text lands on your clipboard.

### `image` — show a picture

Put the image **in the command's folder** and reference it by filename:

```
cmd_esfera/
├── command.md
└── esfera.png
```

```
---
name: esfera
kind: image
usage: /esfera
help: Show the Esfera guide image.
image: esfera.png
holdSeconds: 120
---
```

`image:` also accepts a web URL (`https://…`) or a bundled app asset (`avares://…`). Local files are
confined to the command's own folder (no `..` or absolute paths) for safety.

### `link` — offer a clickable link

```
---
name: wiki
kind: link
usage: /wiki <topic>
help: Search the MapleStory wiki.
link: Search the wiki ↗|https://maplestorywiki.net/w/index.php?search={{args}}
---
```

The pet shows a clickable “Search the wiki ↗” button that opens the URL.

### `prompt` — ask the AI (chatbot)

The body is the **system prompt** (the persona / instructions). What the user types becomes the AI's
user message. Requires the chatbot to be enabled and a provider configured (Settings → Chatbot).

```
---
name: translate
kind: prompt
usage: /translate <text>
help: Translate text to English.
---
You are a translator. Translate the user's message into natural English. Reply with only the translation.
```

**Letting the AI pick the pet's face.** If the reply's first line is `Expression: <name>`, that line is
removed from the spoken text and the pet wears that expression (when the character has it). If it doesn't,
the `reaction:` expression is used as a fallback.

**Weighted outcomes with `roll:`.** Use this when *you* want to control the odds instead of leaving it to
the AI. `roll: A:3, B:1` picks `A` three times as often as `B`, and exposes the result as `{{roll}}`:

```
---
name: fortune
kind: prompt
usage: /fortune [name]
help: Read your daily Maple luck.
holdSeconds: 60
roll: Boom:10, Rare:35, Epic:30, Unique:18, Legendary:7
reaction: smile|60
---
You are the Maple World oracle. Today's luck tier is {{roll}} (do not reveal it was pre-chosen).
The pet can wear one of these faces — pick the best fit and put it on the FIRST line as "Expression: <name>":
{{expressions}}
Read a short, playful fortune for {{name}} on {{date}}.
```

### `pet` — make the pet move and emote

The body is a list of **steps**, one per line: an `op` followed by its arguments. `#` lines and blank
lines are ignored. Up to 32 steps.

| Step | Example | What it does |
|------|---------|--------------|
| `say <text>` | `say Hi {{args}}!` | Sets the speech bubble (supports `{{tokens}}`). |
| `expression <name> [seconds]` | `expression smile 6` | Wear a facial expression. |
| `do_action <name> [once\|hold]` | `do_action alert once` | Play an action animation. (alias: `action`) |
| `walk_to <x>` | `walk_to 400` | Walk to screen x. (alias: `walk`) |
| `move_to <x> <y>` | `move_to 400 200` | Pathfind to a point. (alias: `move`) |
| `face <left\|right>` | `face left` | Turn to face a direction. |
| `stop` | `stop` | Halt and idle. |

```
---
name: wave
kind: pet
usage: /wave [name]
help: The pet smiles and waves hello.
holdSeconds: 6
---
expression smile 6
say Hello! 👋 {{args}}
```

(Action and expression names depend on the worn character — names it doesn't have are skipped.)

### `script` — a sandboxed mini-program (advanced)

For logic the other kinds can't express, `kind: script` runs the body as **JavaScript** in a locked-down
sandbox. It has **no file, network, or system access**, and hard limits (time, memory, statement count) so
a buggy script can't freeze the pet. Must be enabled in Settings (it is by default).

Available inside a script:

| Name | Use |
|------|-----|
| `args` | The argument string the user typed. |
| `say(text)` | What the pet speaks. |
| `clipboard(text)` | Copy text to the clipboard. |
| `image(url)` | Show an image (`avares://` or `http(s)://` only). |
| `link(title, url)` | Add a clickable link. |
| `hold(seconds)` | Override the bubble hold time. |
| `expression(name[, seconds])` | Make the pet wear a face. |
| `action(name[, "once"\|"hold"])` | Play an action. |
| `walk(x)` / `face("left"\|"right")` | Move / turn the pet. |

If you don't call `say(...)`, a value the script *returns* becomes the spoken text.

```
---
name: roll
kind: script
usage: /roll [sides]
help: Roll a die (default 6 sides).
holdSeconds: 8
---
var sides = parseInt(args, 10);
if (!sides || sides < 2) sides = 6;
var n = 1 + Math.floor(Math.random() * sides);
expression(n === sides ? "cheers" : "blink", 8);
say("🎲 You rolled a " + n + " (d" + sides + ")!");
```

---

## 7. The `reaction` field

`reaction` works on **any** kind: after the command runs, the pet wears that expression. Format is an
expression name, optionally with a duration:

```
reaction: cheers
reaction: cheers|6     # hold for 6 seconds
```

For `prompt` commands, `reaction` is the *fallback* face — used only when the AI didn't pick one via an
`Expression:` line.

---

## 8. Managing your commands

- **Add:** drop a folder into the commands directory (or make one with the **Open folder** link in the
  Commands tab). It appears immediately.
- **Enable / disable / delete:** the **Commands** tab (tray → “Commands…”) lists every installed command
  with an on/off switch and a right-click **Delete**.
- **Edit:** change `command.md` and save — the command reloads live.

---

## 9. Rules & gotchas

- **Required:** every command needs a `name` and a `kind`. Each kind needs its payload (clipboard text,
  image, link, or a body for prompt/script/pet). A command that's missing these shows up in the Commands
  tab with a ⚠ error and won't run.
- **Names:** `name` must be letters/digits/`-`/`_`, ≤ 32 chars. Built-in commands (`/rank`, `/clear`,
  `/help`) **can't be overridden** — a command named the same is ignored. If two of your commands share a
  name, the first one loaded wins.
- **Images:** keep them inside the command's folder and reference by filename, so the command is
  self-contained and shareable. Paths with `..` or absolute paths are rejected.
- **Prompt commands** need the chatbot turned on and a provider/API key set (Settings → Chatbot).
- **Script commands** need scripting enabled (Settings) and can only touch the host functions above — no
  internet, no files.
- A broken `command.md` never breaks the others — it's just skipped and flagged.

---

## 10. Cheat sheet

```
---
name: <word>            # required — what you type after '/'
kind: <type>            # required — text | clipboard | image | link | prompt | pet | script
usage: /<word> [...]    # shown in the dropdown
help: <one line>        # shown in the dropdown / /help
aliases: alt1, alt2     # optional extra names
holdSeconds: <number>   # optional bubble hold time
reaction: <expr>[|secs] # optional face afterward

clipboard: <text>       # kind: clipboard
image: <file|url>       # kind: image
link: <Title>|<url>     # kind: link
text: <text>            # kind: text (or use the body)
roll: A:3, B:1          # kind: prompt — weighted {{roll}}
requiresChat: false     # kind: prompt — skip the chatbot-on requirement
---
<body: prompt text | JavaScript | pet steps | spoken text>
```

Tokens: `{{args}}` `{{1}}` `{{2}}` … and (prompt only) `{{name}}` `{{date}}` `{{expressions}}` `{{roll}}`.
