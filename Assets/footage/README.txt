Maple Character Builder — footage export
========================================

Layout
------
  manifest.json                  all animations + ready-to-draw per-frame layer lists
  <Category>/<id>/meta.json      one equipped item's placement data, grouped by pose
  <Category>/<id>/*.png          that item's layer bitmaps (native resolution; deduped
                                 across every pose & frame). ItemEff overlays live in the
                                 SAME folder, named "effect_*.png" (also flagged isEffect).

manifest.json
-------------
  facing / flip / scale / earType
  poses[]                        the exported pose names
  items[]                        { category, equipId, name, folder }
  animations[pose] = {
    canvas{width,height}         fixed render size for this pose
    navel{x,y}                   body-navel pixel within the canvas
    bbox{minX,minY,maxX,maxY,width,height}
    frameCount, frameDelays[]    per-frame hold time (ms)
    playbackCycle[]              frame indices to play in order (e.g. [0,1,2,1])
    frames[] = { frame, delayMs, draw[] }   draw[] is sorted back-to-front
  }
  Each draw[] entry: { image, category, equipId, layer, isEffect, z, order,
                       canvasX, canvasY, width, height }. image is relative to manifest.json.

Coordinates
-----------
All bitmaps are UNFLIPPED (canonical left-facing). A per-item meta.json layer record also
carries the world-frame x,y (top-left in the body-navel-at-(0,0) frame), origin, zSlot and
mapAnchors; the manifest's draw[] carries only the paste-ready canvasX,canvasY
(= x - bbox.minX, y - bbox.minY).

Rendering one frame f of pose P
-------------------------------
  1. Make a transparent image of animations[P].canvas.{width,height}.
  2. For each entry in animations[P].frames[f].draw (already back-to-front),
     paste <entry.image> at (entry.canvasX, entry.canvasY) with alpha-over.
  3. If manifest.flip is true, mirror the whole image horizontally (right-facing).

Playing a pose
--------------
Step through animations[P].playbackCycle, holding frame f for
animations[P].frameDelays[f] milliseconds.
