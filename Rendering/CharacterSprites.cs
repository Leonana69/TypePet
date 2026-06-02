using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MaplePet.Rendering;

/// <summary>
/// The MapleStory character's drawable footage, loaded once from a
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
public sealed class CharacterSprites
{
    /// <summary>One bitmap of one pose-frame, positioned by its top-left offset from the navel.</summary>
    public sealed record Layer(Bitmap Image, double OffsetX, double OffsetY, double Width, double Height);

    /// <summary>
    /// The layers of a single animation frame (back-to-front), how long to hold it, and
    /// <see cref="FootOffset"/> = the navel-to-foot distance for THIS frame (lowest Body/Head pixel
    /// below the navel). Anchoring the navel at <c>feetY - FootOffset</c> keeps the feet planted on
    /// the ground while the body bobs through the cycle, exactly as in the game.
    /// </summary>
    public sealed record Frame(IReadOnlyList<Layer> Layers, double DelayMs, double FootOffset);

    /// <summary>An animation: its frames plus the order they play in (frame indices).</summary>
    public sealed class Pose
    {
        public required IReadOnlyList<Frame> Frames { get; init; }
        public required IReadOnlyList<int> Cycle { get; init; }
    }

    private readonly Dictionary<string, Pose> _poses;

    /// <summary>Half the character's drawn width (logical px), measured about the navel/center across
    /// the rendered poses — used for the drag hit-test, which must cover the visible sprite.</summary>
    public double HalfWidth { get; }

    /// <summary>How far the character is drawn above its feet (logical px) across the rendered poses
    /// — the visible sprite spans <c>[FeetY - HeightAboveFeet, FeetY]</c> vertically.</summary>
    public double HeightAboveFeet { get; }

    public Pose? GetPose(string name) => _poses.TryGetValue(name, out var p) ? p : null;

    private CharacterSprites(Dictionary<string, Pose> poses, double halfWidth, double heightAboveFeet)
    {
        _poses = poses;
        HalfWidth = halfWidth;
        HeightAboveFeet = heightAboveFeet;
    }

    /// <summary>
    /// Load and decode the footage. Returns <c>null</c> (and traces) on any failure so the renderer
    /// can fall back to the placeholder shape instead of crashing the overlay. Must be called after
    /// Avalonia has initialized (e.g. from a Window's OnOpened), since it decodes bitmaps.
    /// <paramref name="hitTestPoses"/> bounds the drag hit-test to the poses actually played
    /// (so the wide attack/prone poses don't inflate it); null measures every pose.
    /// </summary>
    public static CharacterSprites? Load(string assemblyName = "MaplePet", string footageDir = "Assets/footage",
        IReadOnlyCollection<string>? hitTestPoses = null)
    {
        try
        {
            string baseUri = $"avares://{assemblyName}/{footageDir}";

            if (!AssetLoader.Exists(new Uri($"{baseUri}/manifest.json")))
            {
                // Surfaced via Trace (survives Release) because a missing manifest means the overlay
                // silently shows only the placeholder — most likely the footage wasn't shipped/embedded.
                System.Diagnostics.Trace.WriteLine($"[MaplePet] character footage not found at {baseUri}/manifest.json");
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

            var poses = new Dictionary<string, Pose>(StringComparer.OrdinalIgnoreCase);
            foreach (var poseProp in doc.RootElement.GetProperty("animations").EnumerateObject())
            {
                // Tolerate a single malformed pose (e.g. a future export missing a field): skip it and
                // keep the rest of the character, rather than blanking out to the placeholder entirely.
                try
                {
                    poses[poseProp.Name] = ParsePose(poseProp.Value, LoadBitmap);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[MaplePet] skipping malformed pose '{poseProp.Name}': {ex.Message}");
                }
            }

            if (poses.Count == 0) return null;

            ComputeHitBounds(poses, hitTestPoses, out double halfWidth, out double heightAboveFeet);
            return new CharacterSprites(poses, halfWidth, heightAboveFeet);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MaplePet] character footage load failed: {ex}");
            return null;
        }
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
                    // canvasX/Y are the layer's top-left in the pose canvas; the navel sits at
                    // (navelX, navelY) there, so (cx-navelX, cy-navelY) is the navel-relative offset.
                    double oy = cy - navelY;
                    layers.Add(new Layer(loadBitmap(image), cx - navelX, oy, w, h));

                    // Foot line = the body's lowest pixel. Anchor on the Body category only so a long
                    // coat, weapon, or shield hanging below the legs can't lift the feet off the ground.
                    double bottom = oy + h;
                    anyFoot = Math.Max(anyFoot, bottom);
                    if (category.Equals("Body", StringComparison.OrdinalIgnoreCase))
                        bodyFoot = Math.Max(bodyFoot, bottom);
                }
            }
            frames.Add(new Frame(layers, delay, bodyFoot > 0 ? bodyFoot : anyFoot));
        }

        if (cycle.Count == 0)
            for (int i = 0; i < frames.Count; i++) cycle.Add(i);

        return new Pose { Frames = frames, Cycle = cycle };
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
                    halfWidth = Math.Max(halfWidth, Math.Max(Math.Abs(l.OffsetX), Math.Abs(l.OffsetX + l.Width)));
                    heightAboveFeet = Math.Max(heightAboveFeet, frame.FootOffset - l.OffsetY);
                }
        }
        if (halfWidth <= 0) halfWidth = 20;          // fallbacks if the measured set was empty
        if (heightAboveFeet <= 0) heightAboveFeet = 60;
    }
}
