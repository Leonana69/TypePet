#!/usr/bin/env python3
"""Compose the README showcase art from the app's own rendered character frames.

Source frames live in ``Website/assets/sprites/`` — the exact footage the desktop
pet draws (the default character: a teal blob with side fins). This script does no
drawing of its own; it only upscales those frames with nearest-neighbour (so the
pixel art stays crisp) and arranges them, preserving full alpha so the result sits
cleanly on both light and dark GitHub themes.

Outputs (committed):
  docs/poses.png   a labelled strip of the pet's key poses / states
  docs/walk.gif    an animated walk cycle (transparent, shared palette)

Regenerate with:  python3 docs/make-readme-art.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Website", "assets", "sprites")
OUT = os.path.join(ROOT, "docs")
SCALE = 4

INK = (60, 70, 90)          # label colour — legible on white and on dark
GUTTER = 44                 # horizontal gap between poses (post-scale px)
PAD = 16                    # outer breathing room
LABEL_GAP = 18              # gap between sprite baseline and its label


def load(name):
    im = Image.open(os.path.join(SRC, name + ".png")).convert("RGBA")
    return im.resize((im.width * SCALE, im.height * SCALE), Image.NEAREST)


def font(size):
    for path in ("/System/Library/Fonts/SFNSRounded.ttf",
                 "/System/Library/Fonts/SFNS.ttf",
                 "/System/Library/Fonts/Supplemental/Arial.ttf"):
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def make_poses():
    # (frame file, label) — one representative frame per state/action
    cells = [
        ("stand1_f00", "stand"),
        ("walk1_f00", "walk"),
        ("ladder_f00", "climb"),
        ("jump_f00", "jump"),
        ("fly_f00", "fly"),
        ("alert_f00", "alert"),
        ("heal_f00", "heal"),
        ("swingO1_f00", "swing"),
    ]
    sprites = [(load(f), label) for f, label in cells]
    fnt = font(30)

    cell_w = max(s.width for s, _ in sprites) + GUTTER
    sprite_h = max(s.height for s, _ in sprites)
    label_h = 40
    W = cell_w * len(sprites) + PAD * 2
    H = sprite_h + LABEL_GAP + label_h + PAD * 2

    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    baseline = PAD + sprite_h                      # feet sit on this line
    for i, (sp, label) in enumerate(sprites):
        cx = PAD + i * cell_w + cell_w // 2
        img.alpha_composite(sp, (cx - sp.width // 2, baseline - sp.height))
        tb = draw.textbbox((0, 0), label, font=fnt)
        draw.text((cx - (tb[2] - tb[0]) // 2, baseline + LABEL_GAP), label,
                  font=fnt, fill=INK + (255,))
    img.save(os.path.join(OUT, "poses.png"))
    print("wrote docs/poses.png", img.size)


def make_walk():
    # The 4-phase walk cycle (f3 intentionally repeats f1), nearest-neighbour 3x.
    s = 3
    frames = []
    for i in range(4):
        im = Image.open(os.path.join(SRC, "walk1_f%02d.png" % i)).convert("RGBA")
        frames.append(im.resize((im.width * s, im.height * s), Image.NEAREST))
    w = max(f.width for f in frames)
    h = max(f.height for f in frames)
    canvas = []
    for f in frames:
        c = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        c.alpha_composite(f, ((w - f.width) // 2, h - f.height))  # feet-aligned
        canvas.append(c)

    # One shared 255-colour palette across every frame (index 255 reserved for
    # transparency) so colours don't flicker frame to frame.
    TRANS = 255
    union = Image.new("RGB", (w, h * len(canvas)))
    for i, c in enumerate(canvas):
        union.paste(c.convert("RGB"), (0, i * h))
    pal = union.quantize(colors=255, method=Image.MEDIANCUT)

    gif = []
    for c in canvas:
        opaque = c.getchannel("A").point(lambda v: 255 if v >= 128 else 0)
        pf = c.convert("RGB").quantize(palette=pal, dither=Image.Dither.NONE)
        pf.paste(TRANS, mask=opaque.point(lambda v: 255 - v))  # transparent elsewhere
        gif.append(pf)
    gif[0].save(os.path.join(OUT, "walk.gif"), save_all=True, append_images=gif[1:],
                duration=150, loop=0, disposal=2, transparency=TRANS, optimize=False)
    print("wrote docs/walk.gif", canvas[0].size, "x%d frames" % len(gif))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    make_poses()
    make_walk()
