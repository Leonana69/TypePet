// Pig pose renderer — the backend of the web pose editor (tools/pig-editor). The artwork is
// transcribed from the Google Noto Emoji "pig" (Scripts/pig-google.svg, Apache License 2.0)
// and rasterized by the same supersampled renderer the blob uses. The character's frames
// themselves are hand-built by the user in the editor and live in Assets/DefaultCharacters/Pig
// (a built-in character, embedded like the blob in Assets/DefaultCharacters/TypePet) — this file
// no longer GENERATES the character, it only renders single posed frames for the editor's Save.
//
// Usage: dotnet run -- --pig-pose <pose.json> <out.png>  (render one posed frame, tight crop)
//        dotnet run -- --pig-anchors                     (anchor dump; editor parser parity check)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

partial class Program
{
    // ---- pig palette (the SVG's fills) ----
    static readonly (double r, double g, double b) PIG_SKIN  = (255, 210, 177); // #FFD2B1 body
    static readonly (double r, double g, double b) PIG_SHADE = (255, 168, 167); // #FFA8A7 far-side leg
    static readonly (double r, double g, double b) PIG_EAR   = (230,  80, 143); // #E6508F ears
    static readonly (double r, double g, double b) PIG_SNOUT = (255, 125, 134); // #FF7D86 snout
    static readonly (double r, double g, double b) PIG_DARK  = ( 45,  45,  43); // #2D2D2B eyes/nostrils

    // SVG path data, verbatim from Scripts/pig-google.svg (viewBox 0 0 128 128, pig faces left).
    const string PigFarLeg =
        "M70.52,93.98c0,1.5,0.44,5.26,0.44,6.76c0,1.5,0.94,4.5,5.02,4.93c6.31,0.65,7.86-2.64,8.59-6.48 " +
        "c0.99-5.21,0.41-11.4,0.41-11.4L70.52,93.98z";
    const string PigTail =
        "M119.79,19.65c1.78-0.38,4.13,0.19,4.22-1.6c0.11-2.16-2.63-3.57-5.91-2.63 " +
        "c-3.27,0.93-4.32,3.75-4.32,3.75s-3.71-1.73-6.95,0.84c-3.66,2.91-3.94,9.01-3.94,9.01l5.16,4.97c0,0,0.13-5.76,0.94-7.88 " +
        "c1.03-2.72,2.82-2.25,2.82-2.25s-1.97,5.44,0.75,8.73c3.26,3.94,10.51,2.25,9.48-4.41c-0.61-3.96-4.41-6.38-4.41-6.38 " +
        "S118.45,19.93,119.79,19.65z M117.07,30.82c-2.99,0.34-2.06-4.88-1.5-6.38C118.01,25.47,119.51,30.53,117.07,30.82z";
    const string PigBody =
        "M45.97,24.86c18.19-7.4,54.05-9.08,66.17,10.28c12.25,19.57,3.8,32.1,2.39,35.62 " +
        "c-2.02,5.05-3.8,11.54-4.22,14.92c-0.5,3.97-1.13,14.22-1.55,16.33c-0.42,2.11-4.08,5.21-7.88,4.93c-3.8-0.28-6.9-2.67-7.18-4.93 " +
        "c-0.28-2.25,0.84-7.88-1.83-9.85c-2.67-1.97-10.7-0.7-15.49,1.83s-27.4,14.32-52.65,3.24C-1.61,86.1,4.3,62.02,10.5,54.98 " +
        "s5.49-8.45,5.49-8.45s-6.19,1.55-3.8-3.38s8.59-13.09,9.71-13.8c1.13-0.7,4.79-1.83,6.76-0.56c1.97,1.27,1.97,3.1,1.97,3.1 " +
        "S33.87,29.78,45.97,24.86z";
    const string PigFrontLeg =
        "M20.77,93.84c0,0,0.28,9.85,0.42,12.25s0.47,6.29,0.84,8.17c0.42,2.11,2.12,3.32,5.49,3.52 " +
        "c4.65,0.28,6.9-1.69,7.32-3.38c0.42-1.69,0.84-16.89,0.84-16.89L20.77,93.84z";
    const string PigMidLeg =
        "M47.1,98.2c0,0-0.04,4.93,0.14,9.85c0.14,3.8,0.14,8.17,0.56,9.29c0.7,1.87,4.08,2.96,7.04,3.1 " +
        "c3.24,0.15,5.42-1.48,6.76-2.82c0.7-0.7,0.7-4.22,0.84-9.29c0.08-2.96,0.28-11.68,0.28-11.68L47.1,98.2z";
    const string PigEarBack =
        "M62.82,43.58c-0.75-1.63-4.58,0-5.54,2.44c-1.22,3.1,0,6.1,1.78,9.48c1.22,2.31,6.76,9.48,13.14,7.98 " +
        "c7.42-1.75,5.35-13.33,5.07-14.64c-0.28-1.31-1.17-3.06-2.72-2.82c-1.78,0.28-0.93,3.24-0.84,5.44c0.09,2.44,0.19,6.57-2.72,7.23 " +
        "c-3.11,0.7-5.31-1.34-7.04-4.13c-1.69-2.72-3.66-4.79-3.1-7.32C61.2,45.68,63.38,44.8,62.82,43.58z";
    const string PigEarFront =
        "M24.81,40.48c-1.42-0.55-3.1,1.6-4.69,2.91s-4.5,3-5.63,1.6c-1.17-1.47,2.52-5.32,4.69-9.01 " +
        "c1.88-3.19,3.64-6.85,6.1-7.04c3.66-0.28,0.81-1.92-1.88-1.41c-1.97,0.38-4.69,2.16-6.19,5.16S8.57,43.3,10.92,46.77 " +
        "c2.7,4,8.63,2.16,11.26,0C24.81,44.61,26.5,41.14,24.81,40.48z";
    const string PigSnout =
        "M29.5,69.2c-5.16-0.94-13.38,0.5-13.98,6.29c-0.47,4.5,2.44,9.95,11.92,12.01 " +
        "c9.48,2.06,14.17-1.5,14.64-6.57C42.55,75.87,37.2,70.61,29.5,69.2z";
    const string PigNostrilFront =
        "M26.22,77.27c0.2,2.27-0.42,3.91-1.78,4.04c-1.36,0.12-3.27-1.48-3.47-3.75 " +
        "c-0.2-2.27,0.4-4.13,2.16-4.13C24.49,73.43,26.01,75,26.22,77.27z";

    // The two eye centers (SVG user units) — the blink frame replaces the eye dots with lines here.
    static readonly (string Name, double X, double Y)[] PigEyeCenters =
        { ("eye1", 52.65, 66.23), ("eye2", 22.84, 60.19) };

    // ---- pig layout on the 120x116 canvas (navel = (60,70) in the hand-edited manifest) ----
    const double PigTargetH = 56;   // sprite height (the blob is ~62 incl. outline; pigs are long, not tall)
    const double PigFeetY   = 101;  // foot line: exclusive bottom bound of the fill (lowest painted row is 100)

    sealed class PigShape
    {
        public string Name;
        public List<List<(double X, double Y)>> Polys;   // base flatten (no point edits) — cached
        public List<SvgSub> Subs;                        // parsed segments (null for ellipses)
        public List<(double X, double Y)> Anchors;       // anchor points, indexed as the editor indexes them
        public (double r, double g, double b) Color;
        public (double X, double Y) Pivot;   // rotation center (SVG units): hip for legs, base for tail/ears
        public bool IsEye;
    }

    // One path segment: a straight line or a cubic, with the anchor index of its endpoint.
    // c1 belongs to the segment's start anchor, c2 to its end anchor — moving an anchor drags
    // its adjacent control points along, so the curve deforms smoothly.
    sealed class SvgSeg
    {
        public bool Cubic;
        public double C1x, C1y, C2x, C2y;
        public double X, Y;
        public int EndAnchor;
    }

    sealed class SvgSub
    {
        public double StartX, StartY;
        public int StartAnchor;
        public List<SvgSeg> Segs = new List<SvgSeg>();
    }

    static List<PigShape> _pigShapes;
    static double _pigMinX, _pigMaxX, _pigMinY, _pigMaxY;

    // Flatten every SVG shape once, in document (painter's) order, and measure the union bbox.
    static List<PigShape> PigShapes()
    {
        if (_pigShapes != null) return _pigShapes;
        var s = new List<PigShape>();
        void Path(string name, string d, (double r, double g, double b) col, (double X, double Y) pivot)
        {
            var (subs, anchors) = ParseSvgPath(d);
            s.Add(new PigShape
            {
                Name = name, Subs = subs, Anchors = anchors,
                Polys = FlattenSubs(subs, null), Color = col, Pivot = pivot,
            });
        }
        void Ellipse(string name, double a, double b, double c, double dd, double e, double f,
                     double cx, double cy, double rx, double ry, (double r, double g, double b) col,
                     (double X, double Y) pivot, bool eye = false)
            => s.Add(new PigShape
            {
                Name = name,
                Polys = new List<List<(double X, double Y)>> { EllipsePoly(a, b, c, dd, e, f, cx, cy, rx, ry) },
                Color = col,
                Pivot = pivot,
                IsEye = eye,
            });

        // Pivots must match the editor page (tools/pig-editor/index.html): legs hinge at the
        // hip (top center), the tail at its rump attachment, the ears at their head-side base,
        // everything else at its own center.
        Path("farLeg", PigFarLeg, PIG_SHADE, (78, 91));
        Path("tail", PigTail, PIG_SKIN, (105.5, 36));
        Path("body", PigBody, PIG_SKIN, (60, 62));
        Path("frontLeg", PigFrontLeg, PIG_SKIN, (28.2, 95));
        Path("midLeg", PigMidLeg, PIG_SKIN, (54.9, 99.5));
        Path("earBack", PigEarBack, PIG_EAR, (62, 48));
        Path("earFront", PigEarFront, PIG_EAR, (21, 43));
        Path("snout", PigSnout, PIG_SNOUT, (29, 78.5));
        Path("nostril1", PigNostrilFront, PIG_DARK, (24.5, 77.5));
        Ellipse("nostril2", 0.0941, -0.9956, 0.9956, 0.0941, -49.2991, 103.738, 32.35, 78.96, 3.94, 2.62, PIG_DARK, (32.35, 78.96));
        Ellipse("eye1", 0.0985, -0.9951, 0.9951, 0.0985, -18.4459, 112.1081, 52.65, 66.23, 4.83, 4.10, PIG_DARK, (52.65, 66.23), eye: true);
        Ellipse("eye2", 0.0985, -0.9951, 0.9951, 0.0985, -39.304, 76.996, 22.84, 60.19, 4.78, 4.23, PIG_DARK, (22.84, 60.19), eye: true);

        _pigMinX = double.MaxValue; _pigMaxX = double.MinValue;
        _pigMinY = double.MaxValue; _pigMaxY = double.MinValue;
        foreach (var sh in s)
            foreach (var poly in sh.Polys)
                foreach (var p in poly)
                {
                    if (p.X < _pigMinX) _pigMinX = p.X;
                    if (p.X > _pigMaxX) _pigMaxX = p.X;
                    if (p.Y < _pigMinY) _pigMinY = p.Y;
                    if (p.Y > _pigMaxY) _pigMaxY = p.Y;
                }
        _pigShapes = s;
        return s;
    }

    // Paint the whole pig: uniform SVG->canvas map (bbox centered on Cx, feet on PigFeetY),
    // then an optional vertical breathe-stretch about the foot line so the feet stay planted.
    // offsets (SVG units/degrees, keyed by shape name) re-pose individual shapes: rotate by
    // rot about the shape's pivot, then translate by (dx, dy) — the same composition as the
    // editor's `translate(dx dy) rotate(rot px py)`. pointEdits (per-shape anchor-index ->
    // delta, from the editor's point editing) reshape outlines in the part's LOCAL space,
    // before the part transform. Everything is applied before the map so it never disturbs
    // the cached global scale/anchor. rot == 0 skips the pivot round-trip and no-edit shapes
    // use the cached base flatten, so existing poses stay bit-identical.
    static void DrawPig(double stretchY, bool blink,
        Dictionary<string, (double dx, double dy, double rot)> offsets = null,
        Dictionary<string, Dictionary<int, (double dx, double dy)>> pointEdits = null,
        Dictionary<string, Dictionary<int, (double inDx, double inDy, double outDx, double outDy)>> handleEdits = null)
    {
        var shapes = PigShapes();
        double s = PigTargetH / (_pigMaxY - _pigMinY);
        double ox = Cx - s * (_pigMinX + _pigMaxX) / 2;
        double oy = PigFeetY - s * _pigMaxY;

        (double X, double Y) Map((double X, double Y) p)
            => (ox + s * p.X, PigFeetY - (PigFeetY - (oy + s * p.Y)) * stretchY);

        foreach (var sh in shapes)
        {
            if (blink && sh.IsEye) continue;
            (double dx, double dy, double rot) off = default;
            if (offsets != null) offsets.TryGetValue(sh.Name, out off);
            double ca = 1, sa = 0;
            if (off.rot != 0)
            {
                double a = off.rot * Math.PI / 180;
                ca = Math.Cos(a);
                sa = Math.Sin(a);
            }
            Dictionary<int, (double dx, double dy)> pe = null;
            Dictionary<int, (double inDx, double inDy, double outDx, double outDy)> he = null;
            if (pointEdits != null) pointEdits.TryGetValue(sh.Name, out pe);
            if (handleEdits != null) handleEdits.TryGetValue(sh.Name, out he);
            var srcPolys = sh.Polys;
            if (sh.Subs != null && ((pe != null && pe.Count > 0) || (he != null && he.Count > 0)))
                srcPolys = FlattenSubs(sh.Subs, pe, he);
            var polys = new List<List<(double X, double Y)>>(srcPolys.Count);
            foreach (var poly in srcPolys)
            {
                var q = new List<(double X, double Y)>(poly.Count);
                foreach (var p in poly)
                {
                    var t = p;
                    if (off.rot != 0)
                    {
                        double rx = t.X - sh.Pivot.X, ry = t.Y - sh.Pivot.Y;
                        t = (sh.Pivot.X + rx * ca - ry * sa, sh.Pivot.Y + rx * sa + ry * ca);
                    }
                    q.Add(Map((t.X + off.dx, t.Y + off.dy)));
                }
                polys.Add(q);
            }
            FillPolys(polys, sh.Color);
        }

        if (blink)
            foreach (var (name, ecx, ecy) in PigEyeCenters)
            {
                (double dx, double dy, double rot) off = default;
                if (offsets != null) offsets.TryGetValue(name, out off);
                if (off == default)
                {
                    // closed-eye line, sized in SVG units (x s) so it tracks PigTargetH/bbox
                    // changes; this construction is kept bit-identical to the original
                    var e = Map((ecx, ecy));
                    Capsule(e.X - 4.9 * s, e.Y, e.X + 4.9 * s, e.Y + 0.75 * s, 1.7 * s, PIG_DARK, 1);
                }
                else
                {
                    // posed eye: transform the line endpoints exactly like the eye shape
                    // (rotate about the eye center = its pivot, then translate), then map
                    double a = off.rot * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
                    (double X, double Y) T(double px, double py)
                    {
                        double rx = px - ecx, ry = py - ecy;
                        return Map((ecx + rx * ca - ry * sa + off.dx, ecy + rx * sa + ry * ca + off.dy));
                    }
                    var p1 = T(ecx - 4.9, ecy);
                    var p2 = T(ecx + 4.9, ecy + 0.75);
                    Capsule(p1.X, p1.Y, p2.X, p2.Y, 1.7 * s, PIG_DARK, 1);
                }
            }
    }

    // Render one posed frame from a JSON spec — the save path of the web pose editor
    // (tools/pig-editor). Schema: { "stretch": 1.0, "blink": false,
    // "offsets": { "frontLeg": { "dx": -3, "dy": 0, "rot": 12 }, ... } } (SVG units; rot in
    // degrees about the shape's pivot, applied before the translation). Writes the same
    // tight-cropped PNG EmitPose would, and prints the crop placement so a designed pose can
    // be transcribed into Assets/DefaultCharacters/Pig/manifest.json.
    static int PigPose(string jsonPath, string outPng)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        double stretch = root.TryGetProperty("stretch", out var st) ? st.GetDouble() : 1.0;
        bool blink = root.TryGetProperty("blink", out var bl) && bl.GetBoolean();
        var offsets = new Dictionary<string, (double dx, double dy, double rot)>();
        if (root.TryGetProperty("offsets", out var offs))
            foreach (var p in offs.EnumerateObject())
            {
                double dx = p.Value.TryGetProperty("dx", out var dxe) ? dxe.GetDouble() : 0;
                double dy = p.Value.TryGetProperty("dy", out var dye) ? dye.GetDouble() : 0;
                double rot = p.Value.TryGetProperty("rot", out var rote) ? rote.GetDouble() : 0;
                offsets[p.Name] = (dx, dy, rot);
            }

        // "points": { "<part>": { "<anchorIndex>": [dx, dy], ... } } — outline point edits
        var pointEdits = new Dictionary<string, Dictionary<int, (double dx, double dy)>>();
        if (root.TryGetProperty("points", out var ptsEl))
            foreach (var part in ptsEl.EnumerateObject())
            {
                var dict = new Dictionary<int, (double dx, double dy)>();
                foreach (var pp in part.Value.EnumerateObject())
                    dict[int.Parse(pp.Name, CultureInfo.InvariantCulture)] =
                        (pp.Value[0].GetDouble(), pp.Value[1].GetDouble());
                if (dict.Count > 0) pointEdits[part.Name] = dict;
            }

        // "handles": { "<part>": { "<anchorIndex>": { "in": [dx,dy], "out": [dx,dy] } } } —
        // curve-direction tuning: "out" nudges the c1 leaving the anchor, "in" the c2 arriving
        var handleEdits = new Dictionary<string, Dictionary<int, (double inDx, double inDy, double outDx, double outDy)>>();
        if (root.TryGetProperty("handles", out var hdlEl))
            foreach (var part in hdlEl.EnumerateObject())
            {
                var dict = new Dictionary<int, (double inDx, double inDy, double outDx, double outDy)>();
                foreach (var pp in part.Value.EnumerateObject())
                {
                    double inDx = 0, inDy = 0, outDx = 0, outDy = 0;
                    if (pp.Value.TryGetProperty("in", out var inEl)) { inDx = inEl[0].GetDouble(); inDy = inEl[1].GetDouble(); }
                    if (pp.Value.TryGetProperty("out", out var outEl)) { outDx = outEl[0].GetDouble(); outDy = outEl[1].GetDouble(); }
                    dict[int.Parse(pp.Name, CultureInfo.InvariantCulture)] = (inDx, inDy, outDx, outDy);
                }
                if (dict.Count > 0) handleEdits[part.Name] = dict;
            }

        var known = new HashSet<string>();
        foreach (var sh in PigShapes()) known.Add(sh.Name);
        foreach (var k in offsets.Keys)
            if (!known.Contains(k)) Console.WriteLine($"warning: unknown shape '{k}' (known: {string.Join(",", known)})");
        foreach (var k in pointEdits.Keys)
            if (!known.Contains(k)) Console.WriteLine($"warning: unknown shape '{k}' in points");
        foreach (var k in handleEdits.Keys)
            if (!known.Contains(k)) Console.WriteLine($"warning: unknown shape '{k}' in handles");

        Clear();
        DrawPig(stretch, blink, offsets,
            pointEdits.Count > 0 ? pointEdits : null,
            handleEdits.Count > 0 ? handleEdits : null);
        var (rgba, w, h, ox, oy) = Downsample();
        WritePng(outPng, w, h, rgba);
        Console.WriteLine($"pose -> {outPng} ({w}x{h} at canvas {ox},{oy})");
        return 0;
    }

    // Dump every path shape's anchor points as JSON — used to verify the editor's JS parser
    // assigns identical anchor indices (run vs the editor page's ?debug=anchors dump).
    static int PigAnchors()
    {
        var parts = new List<string>();
        foreach (var sh in PigShapes())
        {
            if (sh.Subs == null) continue; // ellipses have no path anchors
            var pts = new List<string>();
            foreach (var (ax, ay) in sh.Anchors)
                pts.Add($"[{ax.ToString("R", CultureInfo.InvariantCulture)},{ay.ToString("R", CultureInfo.InvariantCulture)}]");
            parts.Add($"\"{sh.Name}\":[{string.Join(",", pts)}]");
        }
        Console.WriteLine("{" + string.Join(",", parts) + "}");
        return 0;
    }

    // =====================================================================
    //  SVG path support
    // =====================================================================

    // Parse SVG path data (M/L/H/V/C/S/Z, absolute + relative, implicit command repeats) into
    // absolute line/cubic segments with anchor indices. Anchors are the on-curve points (each
    // subpath's start + every segment endpoint); a segment that lands back on its subpath's
    // start (the explicit closing segment all the pig paths have) reuses the start's anchor, so
    // dragging that anchor never splits the closure seam. The editor (tools/pig-editor) parses
    // the same strings with the same rules — anchor indices MUST stay in sync with it.
    static (List<SvgSub> Subs, List<(double X, double Y)> Anchors) ParseSvgPath(string d)
    {
        var subs = new List<SvgSub>();
        var anchors = new List<(double X, double Y)>();
        SvgSub cur = null;
        int i = 0;
        char cmd = '\0';
        double x = 0, y = 0, startX = 0, startY = 0;
        double refX = 0, refY = 0;   // previous cubic's 2nd control point (for S/s reflection)
        bool prevCubic = false;
        bool closed = false;         // a closepath was just seen; the next drawing command opens a new subpath

        void Skip()
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',' || d[i] == '\t' || d[i] == '\n' || d[i] == '\r')) i++;
        }
        double Num()
        {
            Skip();
            int s0 = i;
            bool dot = false;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot)))
            {
                if (d[i] == '.') dot = true;
                i++;
            }
            return double.Parse(d.Substring(s0, i - s0), CultureInfo.InvariantCulture);
        }
        void NewSub()
        {
            cur = new SvgSub { StartX = x, StartY = y, StartAnchor = anchors.Count };
            anchors.Add((x, y));
            subs.Add(cur);
            closed = false;
        }
        // Per spec, a drawing command right after a closepath (no moveto) starts a NEW subpath
        // at the closed subpath's initial point — without this, post-z segments would be merged
        // into the already-closed polygon and corrupt the fill's crossing counts.
        void EnsureOpen()
        {
            if (closed) NewSub();
        }
        int AnchorFor(double px, double py)
        {
            if (Math.Abs(px - cur.StartX) < 1e-9 && Math.Abs(py - cur.StartY) < 1e-9) return cur.StartAnchor;
            anchors.Add((px, py));
            return anchors.Count - 1;
        }
        void LineTo(double nx, double ny)
        {
            EnsureOpen();
            cur.Segs.Add(new SvgSeg { Cubic = false, X = nx, Y = ny, EndAnchor = AnchorFor(nx, ny) });
            x = nx; y = ny;
            prevCubic = false;
        }
        void CubicTo(double c1x, double c1y, double c2x, double c2y, double ex, double ey)
        {
            EnsureOpen();
            cur.Segs.Add(new SvgSeg
            {
                Cubic = true, C1x = c1x, C1y = c1y, C2x = c2x, C2y = c2y,
                X = ex, Y = ey, EndAnchor = AnchorFor(ex, ey),
            });
            refX = c2x; refY = c2y; x = ex; y = ey;
            prevCubic = true;
        }

        while (true)
        {
            Skip();
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) cmd = d[i++];
            else if (cmd == 'Z' || cmd == 'z')
                throw new NotSupportedException("number after closepath"); // grammar violation; don't loop forever
            // else: a number continues the previous command (implicit repeat)

            switch (cmd)
            {
                case 'M':
                case 'm':
                {
                    double nx = Num(), ny = Num();
                    if (cmd == 'm') { nx += x; ny += y; }
                    x = nx; y = ny; startX = x; startY = y;
                    NewSub();
                    cmd = cmd == 'M' ? 'L' : 'l'; // per spec, extra pairs after a moveto are linetos
                    prevCubic = false;
                    break;
                }
                case 'L':
                case 'l':
                {
                    double nx = Num(), ny = Num();
                    if (cmd == 'l') { nx += x; ny += y; }
                    LineTo(nx, ny);
                    break;
                }
                case 'H':
                case 'h':
                {
                    double nx = Num();
                    if (cmd == 'h') nx += x;
                    LineTo(nx, y);
                    break;
                }
                case 'V':
                case 'v':
                {
                    double ny = Num();
                    if (cmd == 'v') ny += y;
                    LineTo(x, ny);
                    break;
                }
                case 'C':
                case 'c':
                {
                    double c1x = Num(), c1y = Num(), c2x = Num(), c2y = Num(), ex = Num(), ey = Num();
                    if (cmd == 'c') { c1x += x; c1y += y; c2x += x; c2y += y; ex += x; ey += y; }
                    CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                    break;
                }
                case 'S':
                case 's':
                {
                    double c1x = prevCubic ? 2 * x - refX : x;
                    double c1y = prevCubic ? 2 * y - refY : y;
                    double c2x = Num(), c2y = Num(), ex = Num(), ey = Num();
                    if (cmd == 's') { c2x += x; c2y += y; ex += x; ey += y; }
                    CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                    break;
                }
                case 'Z':
                case 'z':
                    x = startX; y = startY;       // closure edge is implicit in the fill's wraparound
                    prevCubic = false;
                    closed = true;
                    break;
                default:
                    throw new NotSupportedException($"SVG path command '{cmd}'");
            }
        }
        return (subs, anchors);
    }

    // Flatten parsed segments to one point list per subpath, optionally with per-anchor deltas
    // (the editor's point edits) and per-anchor HANDLE deltas (curve-direction tuning): an
    // anchor's delta moves the anchor and its adjacent control points; a handle delta then
    // nudges one control point independently — "out" is the c1 of the segment leaving the
    // anchor, "in" the c2 of the segment arriving at it. With no edits this reproduces the
    // original flatten bit-for-bit.
    static List<List<(double X, double Y)>> FlattenSubs(List<SvgSub> subs,
        Dictionary<int, (double dx, double dy)> edits,
        Dictionary<int, (double inDx, double inDy, double outDx, double outDy)> handles = null)
    {
        (double dx, double dy) D(int a)
            => edits != null && edits.TryGetValue(a, out var de) ? de : (0.0, 0.0);
        (double inDx, double inDy, double outDx, double outDy) H(int a)
            => handles != null && handles.TryGetValue(a, out var he) ? he : default;

        var outp = new List<List<(double X, double Y)>>(subs.Count);
        foreach (var sub in subs)
        {
            var d0 = D(sub.StartAnchor);
            double px = sub.StartX + d0.dx, py = sub.StartY + d0.dy;
            var pts = new List<(double X, double Y)> { (px, py) };
            int prevAnchor = sub.StartAnchor;
            foreach (var seg in sub.Segs)
            {
                var ds = D(prevAnchor);
                var de = D(seg.EndAnchor);
                double ex = seg.X + de.dx, ey = seg.Y + de.dy;
                if (seg.Cubic)
                {
                    var hs = H(prevAnchor);     // start anchor's "out" handle moves c1
                    var he = H(seg.EndAnchor);  // end anchor's "in" handle moves c2
                    FlattenCubic(pts, px, py,
                        seg.C1x + ds.dx + hs.outDx, seg.C1y + ds.dy + hs.outDy,
                        seg.C2x + de.dx + he.inDx, seg.C2y + de.dy + he.inDy, ex, ey);
                }
                else
                    pts.Add((ex, ey));
                px = ex; py = ey;
                prevAnchor = seg.EndAnchor;
            }
            outp.Add(pts);
        }
        return outp;
    }

    static void FlattenCubic(List<(double X, double Y)> pts,
        double x0, double y0, double c1x, double c1y, double c2x, double c2y, double x1, double y1)
    {
        const int N = 28;
        for (int k = 1; k <= N; k++)
        {
            double t = (double)k / N, u = 1 - t;
            pts.Add((u * u * u * x0 + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x1,
                     u * u * u * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y1));
        }
    }

    // <ellipse> under an affine matrix(a b c d e f): sample parametrically, transform each point.
    static List<(double X, double Y)> EllipsePoly(double a, double b, double c, double d, double e, double f,
        double cx, double cy, double rx, double ry)
    {
        const int N = 64;
        var pts = new List<(double X, double Y)>(N);
        for (int k = 0; k < N; k++)
        {
            double t = Math.PI * 2 * k / N;
            double px = cx + rx * Math.Cos(t), py = cy + ry * Math.Sin(t);
            pts.Add((a * px + c * py + e, b * px + d * py + f));
        }
        return pts;
    }

    // Fill a set of canvas-space polygons (one shape's subpaths) with the NONZERO winding rule
    // (the SVG default — none of the pig's paths set fill-rule) by scanline at supersample
    // resolution. An opposite-winding subpath inside another cuts a hole (the tail curl).
    static void FillPolys(List<List<(double X, double Y)>> polys, (double r, double g, double b) col)
    {
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var poly in polys)
            foreach (var p in poly)
            {
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        if (minY > maxY) return;

        int py0 = Math.Max(0, (int)Math.Floor(minY * SS));
        int py1 = Math.Min(H - 1, (int)Math.Ceiling(maxY * SS));
        var xs = new List<(double X, int Dir)>();
        for (int py = py0; py <= py1; py++)
        {
            double ly = (py + 0.5) / SS;
            xs.Clear();
            foreach (var poly in polys)
            {
                int n = poly.Count;
                for (int k = 0; k < n; k++)
                {
                    var a = poly[k];
                    var b = poly[(k + 1) % n];
                    if (a.Y <= ly && b.Y > ly)
                        xs.Add((a.X + (ly - a.Y) / (b.Y - a.Y) * (b.X - a.X), 1));
                    else if (b.Y <= ly && a.Y > ly)
                        xs.Add((a.X + (ly - a.Y) / (b.Y - a.Y) * (b.X - a.X), -1));
                }
            }
            xs.Sort((p, q) => p.X.CompareTo(q.X));
            int wind = 0;
            for (int k = 0; k + 1 < xs.Count; k++)
            {
                wind += xs[k].Dir;
                if (wind == 0) continue;
                int px0 = Math.Max(0, (int)Math.Ceiling(xs[k].X * SS - 0.5));
                int px1 = Math.Min(W - 1, (int)Math.Floor(xs[k + 1].X * SS - 0.5));
                for (int px = px0; px <= px1; px++) Blend(px, py, col, 1);
            }
        }
    }
}
