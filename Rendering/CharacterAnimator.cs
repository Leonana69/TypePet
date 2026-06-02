using MaplePet.Engine;

namespace MaplePet.Rendering;

/// <summary>
/// Tracks which animation frame the character is showing. It maps the pet's <see cref="PetState"/>
/// to a footage pose, then walks that pose's playback cycle in real time, holding each frame for
/// its authored delay. Switching state restarts the new pose from its first frame.
///
/// It only chooses the frame; <see cref="PetRenderer"/> decides where/how to draw it.
/// </summary>
public sealed class CharacterAnimator
{
    private string _pose = "stand1";
    private int _cycleIndex;    // position within the pose's playback cycle
    private double _elapsedMs;  // time spent on the current frame

    /// <summary>The active footage pose name (e.g. "stand1", "walk1").</summary>
    public string Pose => _pose;

    /// <summary>The resolved frame index into the pose's <c>Frames</c> list.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>Advance the animation by <paramref name="dt"/> seconds for the pet's current state.</summary>
    public void Update(CharacterSprites? sprites, PetState state, double dt)
    {
        string desired = PoseFor(state);
        if (desired != _pose)
        {
            _pose = desired;
            _cycleIndex = 0;
            _elapsedMs = 0;
            FrameIndex = 0;
        }

        var pose = sprites?.GetPose(_pose);
        if (pose is null || pose.Cycle.Count == 0 || pose.Frames.Count == 0)
        {
            FrameIndex = 0;
            return;
        }

        _elapsedMs += dt * 1000.0;
        // Hold each frame for its delay; the while-loop catches up if a tick covered several frames
        // (or a frame has a tiny/zero delay), so we never spin forever.
        FrameIndex = Clamp(pose.Cycle[_cycleIndex % pose.Cycle.Count], pose.Frames.Count);
        double delay = FrameDelay(pose, FrameIndex);
        while (_elapsedMs >= delay)
        {
            _elapsedMs -= delay;
            _cycleIndex = (_cycleIndex + 1) % pose.Cycle.Count;
            FrameIndex = Clamp(pose.Cycle[_cycleIndex], pose.Frames.Count);
            delay = FrameDelay(pose, FrameIndex);
        }
    }

    private static double FrameDelay(CharacterSprites.Pose pose, int frame)
    {
        double d = pose.Frames[frame].DelayMs;
        return d > 0 ? d : 150; // guard against zero/negative authored delays
    }

    private static int Clamp(int frame, int count) => frame < 0 ? 0 : frame >= count ? count - 1 : frame;

    private static string PoseFor(PetState state) => state switch
    {
        PetState.Walk => "walk1",
        PetState.Rope => "ladder", // window side-edges read as ladders; swap to "rope" for the rope pose
        PetState.Jump => "jump",
        _ => "stand1",
    };

    /// <summary>The poses this animator can actually display (one per <see cref="PetState"/>).
    /// Used to size the drag hit-test to only the poses that are played. Keep in sync with
    /// <see cref="PoseFor"/>.</summary>
    public static readonly IReadOnlyCollection<string> ActivePoses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stand1", "walk1", "ladder", "jump" };
}
