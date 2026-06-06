---
name: roll
kind: script
usage: /roll [sides]
help: Roll a die (default 6 sides). Example of a sandboxed script command.
holdSeconds: 8
---
// Sandboxed JavaScript: only the host API (say/clipboard/expression/…) and `args` are available —
// no file, network, or system access, with hard time/memory limits.
var sides = parseInt(args, 10);
if (!sides || sides < 2) sides = 6;
var n = 1 + Math.floor(Math.random() * sides);
expression(n === sides ? "cheers" : "blink", 8);
say("🎲 You rolled a " + n + " (d" + sides + ")!");
