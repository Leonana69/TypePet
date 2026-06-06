---
name: wave
kind: pet
usage: /wave [name]
help: The pet smiles and waves hello.
holdSeconds: 6
---
# one step per line: op args  (say takes the rest of the line; # lines are comments)
expression smile 6
say Hello! 👋 {{args}}
