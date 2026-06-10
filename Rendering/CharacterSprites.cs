using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TypePet.Rendering;

/// <summary>
/// The character's drawable sprite footage, loaded once from a
/// <c>manifest.json</c> (exported by maple-character-builder). We render every equipped layer the
/// manifest lists — Body, Head, Hair, Cap, Face, FaceAcc, Earring, Longcoat, Shoes, Weapon, Shield,
/// and any item effects — in the manifest's order, which is already sorted back-to-front by z. Each
/// layer is stored as an offset from the body navel. The bundled Body+Head default character is just
/// a manifest that lists only those two categories, so the same loader renders it as a bald avatar.
///
/// Coordinates: every pose has a fixed canvas with the body navel at canvas pixel
/// <c>navel{x,y}</c>. We re-express each layer as an offset from the navel (the one stable anchor
/// shared by every pose/frame), so the renderer can pin the navel to a world point and paste the
/// layers around it. Bitmaps are the canonical, unflipped (left-facing) artwork; the renderer
/// mirrors them for right-facing.
/// </summary>
public sealed class CharacterSprites : IDisposable
{
    /// <summary>One bitmap of one pose-frame, positioned by its top-left offset from the navel.
    /// <paramref name="IsEffect"/> marks item-effect auras/glows, which render but are excluded from
    /// the drag hit-test (they can be far larger than the character). <paramref name="IsFace"/> marks
    /// the neutral Face layer, so the renderer can substitute a chosen <see cref="Expression"/> in its
    /// place (at the frame's <see cref="Frame.FaceAnchor"/>) without disturbing the layer order.</summary>
    public sealed record Layer(Bitmap Image, double OffsetX, double OffsetY, double Width, double Height, bool IsEffect, bool IsFace);

    /// <summary>
    /// The layers of a single animation frame (back-to-front), how long to hold it,
    /// <see cref="FootOffset"/> = the navel-to-foot distance for THIS frame (lowest Body/Head pixel
    /// below the navel), and <see cref="FaceAnchor"/> = the navel-relative "brow" point this frame
    /// pins the face to (null when the export predates face anchors). Anchoring the navel at
    /// <c>feetY - FootOffset</c> keeps the feet planted on the ground while the body bobs through the
    /// cycle, exactly as in the game.
    /// </summary>
    public sealed record Frame(IReadOnlyList<Layer> Layers, double DelayMs, double FootOffset, (double X, double Y)? FaceAnchor);

    /// <summary>An animation: its frames plus the order they play in (frame indices).</summary>
    public sealed class Pose
    {
        public required IReadOnlyList<Frame> Frames { get; init; }
        public required IReadOnlyList<int> Cycle { get; init; }
    }

    /// <summary>One frame of a face expression: the bitmap, its size, the
    /// <see cref="AnchorOffsetX"/>/<see cref="AnchorOffsetY"/> point within the image that sits on the
    /// pose's "brow" anchor (so faces of different sizes/decorations all line up), plus how long to hold
    /// it. This is the exporter's brow-aligned <c>anchorOffset</c>, NOT the raw image <c>origin</c> —
    /// expressions padded above the eyes (hearts, sweat, sparkles) have an origin that points into the
    /// padding and would render the face too high.</summary>
    public sealed record ExpressionFrame(Bitmap Image, double AnchorOffsetX, double AnchorOffsetY, double Width, double Height, double DelayMs);

    /// <summary>A swappable face expression (e.g. "smile", "blink", "love") — a list of frames played
    /// in order, looping, by their authored delays. Drawn in place of the neutral Face layer, anchored
    /// to each pose-frame's brow point.</summary>
    public sealed class Expression
    {
        public required string Name { get; init; }
        public required IReadOnlyList<ExpressionFrame> Frames { get; init; }
    }

    private readonly Dictionary<string, Pose> _poses;
    private readonly Dictionary<string, Expression> _expressions;
    private readonly Bitmap[] _ownedBitmaps; // distinct decoded bitmaps, disposed together

    /// <summary>Half the character's drawn width (logical px), measured about the navel/center across
    /// the rendered poses — used for the drag hit-test, which must cover the visible sprite.</summary>
    public double HalfWidth { get; }

    /// <summary>How far the character is drawn above its feet (logical px) across the rendered poses
    /// — the visible sprite spans <c>[FeetY - HeightAboveFeet, FeetY]</c> vertically.</summary>
    public double HeightAboveFeet { get; }

    public Pose? GetPose(string name) => _poses.TryGetValue(name, out var p) ? p : null;

    /// <summary>The names of every loaded footage pose (e.g. "stand1", "walk1", "prone", "alert").
    /// Used by the control API to advertise only the actions this character actually supports.</summary>
    public IReadOnlyCollection<string> PoseNames => _poses.Keys;

    /// <summary>The named face expression, or null if this character doesn't define it (or expressions
    /// weren't loaded).</summary>
    public Expression? GetExpression(string name) => _expressions.TryGetValue(name, out var e) ? e : null;

    /// <summary>The names of every loaded face expression (includes "default"). Empty when the
    /// character has no Face item with expression footage, or expressions weren't requested.</summary>
    public IReadOnlyCollection<string> ExpressionNames => _expressions.Keys;

    /// <summary>True when at least one swappable face expression was loaded.</summary>
    public bool HasExpressions => _expressions.Count > 0;

    private CharacterSprites(Dictionary<string, Pose> poses, Dictionary<string, Expression> expressions,
        ICollection<Bitmap> decodedBitmaps, double halfWidth, double heightAboveFeet)
    {
        _poses = poses;
        _expressions = expressions;
        HalfWidth = halfWidth;
        HeightAboveFeet = heightAboveFeet;

        // Own EVERY bitmap the loader decoded this load — its cache is the authoritative set — rather
        // than re-deriving the list by walking the committed poses/expressions. A pose or expression
        // that threw partway through parsing is discarded, yet the PNGs it had already decoded still
        // sit in that cache; sweeping it guarantees those orphans are disposed too (nothing else can
        // reach them). Each distinct PNG path decodes to exactly one instance, so the values are
        // already distinct — no de-duplication needed.
        _ownedBitmaps = new Bitmap[decodedBitmaps.Count];
        decodedBitmaps.CopyTo(_ownedBitmaps, 0);
    }

    /// <summary>Release the decoded bitmaps (native/GPU-backed; Avalonia <see cref="Bitmap"/> has no
    /// finalizer, so they must be disposed explicitly). Safe to call once; the sprites are unusable
    /// afterward. Each <see cref="CharacterSprites"/> owns its own freshly-decoded bitmaps — instances
    /// aren't shared across loads — so disposing one never affects another.</summary>
    public void Dispose()
    {
        foreach (var bmp in _ownedBitmaps)
        {
            try { bmp.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Load and decode footage embedded as Avalonia resources (the bundled Assets/DefaultCharacters/*).
    /// Returns <c>null</c> (and traces) on any failure so the renderer can fall back to the placeholder
    /// shape instead of crashing the overlay. Must be called after Avalonia has initialized (e.g. from a
    /// Window's OnOpened), since it decodes bitmaps. <paramref name="hitTestPoses"/> bounds the drag
    /// hit-test to the poses actually played (so the wide attack/prone poses don't inflate it); null
    /// measures every pose. <paramref name="posesToLoad"/> restricts which poses are decoded at all
    /// (null = decode every pose) — a big saving when only a few poses are shown (the live pet plays a
    /// handful; a card thumbnail needs just one), since each pose decodes its own PNGs.
    /// <paramref name="loadExpressions"/> additionally loads the Face item's swappable expressions
    /// (only the live pet needs them — thumbnails skip the extra decode).
    /// </summary>
    public static CharacterSprites? Load(string assemblyName = "TypePet", string footageDir = "Assets/footage",
        IReadOnlyCollection<string>? hitTestPoses = null, IReadOnlyCollection<string>? posesToLoad = null,
        bool loadExpressions = false)
    {
        try
        {
            string baseUri = $"avares://{assemblyName}/{footageDir}";

            if (!AssetLoader.Exists(new Uri($"{baseUri}/manifest.json")))
            {
                // Surfaced via Trace (survives Release) because a missing manifest means the overlay
                // silently shows only the placeholder — most likely the footage wasn't shipped/embedded.
                System.Diagnostics.Trace.WriteLine($"[TypePet] character footage not found at {baseUri}/manifest.json");
                return null;
            }

            using var manifestStream = AssetLoader.Open(new Uri($"{baseUri}/manifest.json"));
            using var doc = JsonDocument.Parse(manifestStream);

            // Decode each referenced PNG once; the same bitmap (e.g. head__stand1_f00) recurs across
            // many poses and frames.
            var bitmaps = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            Bitmap LoadBitmap(string relativePath)
            {
                if (!bitmaps.TryGetValue(relativePath, out var bmp))
                {
                    using var s = AssetLoader.Open(new Uri($"{baseUri}/{relativePath}"));
                    bmp = new Bitmap(s);
                    bitmaps[relativePath] = bmp;
                }
                return bmp;
            }

            JsonDocument? TryLoadJson(string relativePath)
            {
                var uri = new Uri($"{baseUri}/{relativePath}");
                if (!AssetLoader.Exists(uri)) return null;
                using var s = AssetLoader.Open(uri);
                return JsonDocument.Parse(s);
            }

            return Build(doc, LoadBitmap, TryLoadJson, bitmaps, hitTestPoses, posesToLoad, loadExpressions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[TypePet] character footage load failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Load and decode footage from a directory on disk (a user character imported from a zip — a
    /// <c>manifest.json</c> alongside its item folders, exactly like the embedded footage). Same
    /// contract as <see cref="Load"/>: returns <c>null</c> and traces on any failure. The manifest's
    /// <c>image</c> paths are slash-separated and resolved relative to <paramref name="directory"/>.
    /// </summary>
    public static CharacterSprites? LoadFromDirectory(string directory,
        IReadOnlyCollection<string>? hitTestPoses = null, IReadOnlyCollection<string>? posesToLoad = null,
        bool loadExpressions = false)
    {
        try
        {
            string manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                System.Diagnostics.Trace.WriteLine($"[TypePet] character manifest not found at {manifestPath}");
                return null;
            }

            using var manifestStream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(manifestStream);

            var bitmaps = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            Bitmap LoadBitmap(string relativePath)
            {
                if (!bitmaps.TryGetValue(relativePath, out var bmp))
                {
                    string full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    using var s = File.OpenRead(full);
                    bmp = new Bitmap(s);
                    bitmaps[relativePath] = bmp;
                }
                return bmp;
            }

            JsonDocument? TryLoadJson(string relativePath)
            {
                string full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return null;
                using var s = File.OpenRead(full);
                return JsonDocument.Parse(s);
            }

            return Build(doc, LoadBitmap, TryLoadJson, bitmaps, hitTestPoses, posesToLoad, loadExpressions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[TypePet] character footage load failed from '{directory}': {ex}");
            return null;
        }
    }

    /// <summary>
    /// Parse every animation in the manifest into poses (decoding bitmaps via
    /// <paramref name="loadBitmap"/>), measure the hit bounds, and assemble the sprites. Shared by the
    /// embedded-resource (<see cref="Load"/>) and on-disk (<see cref="LoadFromDirectory"/>) loaders,
    /// which differ only in how a relative image path becomes a decoded bitmap.
    /// </summary>
    private static CharacterSprites? Build(JsonDocument doc, Func<string, Bitmap> loadBitmap,
        Func<string, JsonDocument?> tryLoadJson, Dictionary<string, Bitmap> decoded,
        IReadOnlyCollection<string>? hitTestPoses, IReadOnlyCollection<string>? posesToLoad, bool loadExpressions)
    {
        var poses = new Dictionary<string, Pose>(StringComparer.OrdinalIgnoreCase);
        foreach (var poseProp in doc.RootElement.GetProperty("animations").EnumerateObject())
        {
            // Decode only the poses that will actually be shown — each pose decodes its own PNGs, so
            // skipping the rest avoids loading hundreds of images for a character that only plays a
            // few poses (or one, for a thumbnail). null = decode everything (the dev pose renderer).
            if (posesToLoad is not null && !posesToLoad.Contains(poseProp.Name)) continue;

            // Tolerate a single malformed pose (e.g. a future export missing a field): skip it and
            // keep the rest of the character, rather than blanking out to the placeholder entirely.
            try
            {
                poses[poseProp.Name] = ParsePose(poseProp.Value, loadBitmap);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[TypePet] skipping malformed pose '{poseProp.Name}': {ex.Message}");
            }
        }

        if (poses.Count == 0)
        {
            // Nothing usable to render — no CharacterSprites will be constructed to own them, so free
            // whatever PNGs the failed poses managed to decode before bailing.
            foreach (var bmp in decoded.Values)
                try { bmp.Dispose(); } catch { /* best effort */ }
            return null;
        }

        // The Face item carries the swappable expressions in its own meta.json (alongside the per-pose
        // faces the manifest already lists). Load them only when asked, and degrade silently to "no
        // expressions" for a character without a Face item or from an older export.
        var expressions = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
        if (loadExpressions && FindFaceItemFolder(doc) is string faceFolder)
        {
            using var meta = tryLoadJson($"{faceFolder}/meta.json");
            if (meta is not null) ParseExpressions(meta, faceFolder, loadBitmap, expressions);
        }

        ComputeHitBounds(poses, hitTestPoses, out double halfWidth, out double heightAboveFeet);
        return new CharacterSprites(poses, expressions, decoded.Values, halfWidth, heightAboveFeet);
    }

    private static Pose ParsePose(JsonElement anim, Func<string, Bitmap> loadBitmap)
    {
        var navel = anim.GetProperty("navel");
        double navelX = navel.GetProperty("x").GetDouble();
        double navelY = navel.GetProperty("y").GetDouble();

        var cycle = new List<int>();
        if (anim.TryGetProperty("playbackCycle", out var pc) && pc.ValueKind == JsonValueKind.Array)
            foreach (var e in pc.EnumerateArray()) cycle.Add(e.GetInt32());

        var frames = new List<Frame>();
        foreach (var fr in anim.GetProperty("frames").EnumerateArray())
        {
            double delay = fr.TryGetProperty("delayMs", out var d) ? d.GetDouble() : 150;

            // The brow anchor this frame pins the face to (canvas coords), re-expressed navel-relative
            // so the renderer can place a chosen expression there. Absent in pre-expression exports.
            (double X, double Y)? faceAnchor = null;
            if (fr.TryGetProperty("faceAnchor", out var fa) && fa.ValueKind == JsonValueKind.Object)
                faceAnchor = (fa.GetProperty("x").GetDouble() - navelX, fa.GetProperty("y").GetDouble() - navelY);

            var layers = new List<Layer>();
            double bodyFoot = 0, anyFoot = 0;
            if (fr.TryGetProperty("draw", out var draws) && draws.ValueKind == JsonValueKind.Array)
            {
                foreach (var dr in draws.EnumerateArray())
                {
                    // Render every equipped layer the manifest lists (Body, Head, Hair, Cap, Face,
                    // Longcoat, Weapon, Shield, effects, ...). draw[] is already back-to-front.
                    string category = dr.GetProperty("category").GetString() ?? "";
                    string image = dr.GetProperty("image").GetString()!;
                    double cx = dr.GetProperty("canvasX").GetDouble();
                    double cy = dr.GetProperty("canvasY").GetDouble();
                    double w = dr.GetProperty("width").GetDouble();
                    double h = dr.GetProperty("height").GetDouble();
                    // Item-effect overlays: flagged isEffect, or the dedicated "effect" layer slot
                    // (some exports leave isEffect=false but still name the layer "effect").
                    string layerName = dr.TryGetProperty("layer", out var ln) ? (ln.GetString() ?? "") : "";
                    bool isEffect = (dr.TryGetProperty("isEffect", out var ie) && ie.ValueKind == JsonValueKind.True)
                                    || layerName.Equals("effect", StringComparison.OrdinalIgnoreCase);
                    // The neutral face layer (not FaceAcc): the renderer swaps a chosen expression in
                    // here, at faceAnchor, while leaving the rest of the layer stack untouched.
                    bool isFace = category.Equals("Face", StringComparison.OrdinalIgnoreCase);
                    // canvasX/Y are the layer's top-left in the pose canvas; the navel sits at
                    // (navelX, navelY) there, so (cx-navelX, cy-navelY) is the navel-relative offset.
                    double oy = cy - navelY;
                    layers.Add(new Layer(loadBitmap(image), cx - navelX, oy, w, h, isEffect, isFace));

                    // Foot line = the body's lowest pixel. Anchor on the Body category only so a long
                    // coat, weapon, or shield hanging below the legs can't lift the feet off the ground.
                    double bottom = oy + h;
                    anyFoot = Math.Max(anyFoot, bottom);
                    if (category.Equals("Body", StringComparison.OrdinalIgnoreCase))
                        bodyFoot = Math.Max(bodyFoot, bottom);
                }
            }
            frames.Add(new Frame(layers, delay, bodyFoot > 0 ? bodyFoot : anyFoot, faceAnchor));
        }

        if (cycle.Count == 0)
            for (int i = 0; i < frames.Count; i++) cycle.Add(i);

        return new Pose { Frames = frames, Cycle = cycle };
    }

    /// <summary>The footage-relative folder of the equipped Face item (e.g. <c>Face/00027244</c>), whose
    /// <c>meta.json</c> holds the expressions — or null if no Face is equipped.</summary>
    private static string? FindFaceItemFolder(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var it in items.EnumerateArray())
        {
            // Guard the string reads against ValueKind — GetString() throws on a non-string element, and
            // an uncaught throw here would discard the whole (otherwise valid) character, not just the
            // expressions. Degrade locally instead, like ParsePose/ParseExpressions.
            if (it.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String
                && string.Equals(c.GetString(), "Face", StringComparison.OrdinalIgnoreCase)
                && it.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String
                && f.GetString() is { Length: > 0 } folder)
                return folder;
        }
        return null;
    }

    /// <summary>
    /// Parse the Face <c>meta.json</c>'s <c>expressions</c> map into drawable expressions, decoding
    /// each frame's PNG via <paramref name="loadBitmap"/> (resolved under <paramref name="itemFolder"/>).
    /// One malformed expression is skipped rather than failing the whole set.
    /// </summary>
    private static void ParseExpressions(JsonDocument meta, string itemFolder, Func<string, Bitmap> loadBitmap,
        Dictionary<string, Expression> into)
    {
        if (!meta.RootElement.TryGetProperty("expressions", out var exprs) || exprs.ValueKind != JsonValueKind.Object)
            return;

        foreach (var ep in exprs.EnumerateObject())
        {
            try
            {
                var frames = new List<ExpressionFrame>();
                foreach (var fr in ep.Value.GetProperty("frames").EnumerateArray())
                {
                    string image = fr.GetProperty("image").GetString()!;
                    double w = fr.GetProperty("width").GetDouble();
                    double h = fr.GetProperty("height").GetDouble();
                    // Place the image by its brow-aligned anchorOffset, not the raw image origin — for
                    // expressions padded above the eyes (hearts, sweat, sparkles) the two differ and the
                    // origin sits in the padding, rendering the face too high. Fall back to origin for
                    // older exports that don't carry anchorOffset.
                    var anchor = fr.TryGetProperty("anchorOffset", out var ao) && ao.ValueKind == JsonValueKind.Object
                        ? ao : fr.GetProperty("origin");
                    double ax = anchor.GetProperty("x").GetDouble();
                    double ay = anchor.GetProperty("y").GetDouble();
                    double delay = fr.TryGetProperty("delayMs", out var d) ? d.GetDouble() : 100;
                    frames.Add(new ExpressionFrame(loadBitmap($"{itemFolder}/{image}"), ax, ay, w, h, delay));
                }
                if (frames.Count > 0)
                    into[ep.Name] = new Expression { Name = ep.Name, Frames = frames };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[TypePet] skipping malformed expression '{ep.Name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Measure the drawn character's extent (about the navel, and above the feet) over the poses that
    /// are actually played, so the drag hit-test can cover the whole visible sprite regardless of
    /// pose or facing. The mirror is about the navel, so a symmetric half-width covers both facings.
    /// </summary>
    private static void ComputeHitBounds(Dictionary<string, Pose> poses, IReadOnlyCollection<string>? only,
        out double halfWidth, out double heightAboveFeet)
    {
        halfWidth = 0;
        heightAboveFeet = 0;
        foreach (var (name, pose) in poses)
        {
            if (only is not null && !only.Contains(name)) continue;
            foreach (var frame in pose.Frames)
                foreach (var l in frame.Layers)
                {
                    if (l.IsEffect) continue; // effect auras/glows can dwarf the character; not grabbable area
                    halfWidth = Math.Max(halfWidth, Math.Max(Math.Abs(l.OffsetX), Math.Abs(l.OffsetX + l.Width)));
                    heightAboveFeet = Math.Max(heightAboveFeet, frame.FootOffset - l.OffsetY);
                }
        }
        if (halfWidth <= 0) halfWidth = 20;          // fallbacks if the measured set was empty
        if (heightAboveFeet <= 0) heightAboveFeet = 60;
    }
}
