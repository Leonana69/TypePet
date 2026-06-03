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

    // Commanded action pose (control API), overriding the state-driven pose while set.
    private string? _action;            // action pose name; null = follow the locomotion state
    private ActionMode _actionMode = ActionMode.Once;
    private bool _actionCompleted;      // a Once action finished its single pass (read once via ActionJustCompleted)

    // Face expression: a persistent base (set by the control API, optionally auto-clearing) plus a
    // transient overlay (the random face shown while dragging) that takes precedence and reverts to
    // the base when cleared — so an LLM-set expression survives a user drag.
    private string? _baseExpression;
    private double _baseExpressionRemainingMs = -1; // <0 = no auto-clear
    private string? _transientExpression;
    private string? _activeExpr;        // the effective expression currently being looped (change detection)
    private int _exprCycleIndex;        // position within the expression's frame loop
    private double _exprElapsedMs;       // time spent on the current expression frame

    /// <summary>The active footage pose name (e.g. "stand1", "walk1", or a commanded action pose).</summary>
    public string Pose => _pose;

    /// <summary>The resolved frame index into the pose's <c>Frames</c> list.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>The commanded action pose name, or null when following the locomotion state. A
    /// <see cref="ActionMode.Once"/> action clears itself to null when its single pass completes.</summary>
    public string? Action => _action;

    /// <summary>The effective face expression name (transient drag face, else the base expression),
    /// or null when showing the character's neutral face.</summary>
    public string? Expression => _transientExpression ?? _baseExpression;

    /// <summary>The resolved frame index into the active expression's <c>Frames</c> list.</summary>
    public int ExpressionFrameIndex { get; private set; }

    /// <summary>Play <paramref name="pose"/> as a commanded action, overriding the state-driven pose
    /// (null clears it, reverting to the locomotion pose). Restarts playback from the first frame.</summary>
    public void SetAction(string? pose, ActionMode mode = ActionMode.Once)
    {
        if (_action == pose && _actionMode == mode) return;
        _action = pose;
        _actionMode = mode;
        _actionCompleted = false;
        _cycleIndex = 0;
        _elapsedMs = 0;
        FrameIndex = 0;
    }

    /// <summary>True exactly once after a <see cref="ActionMode.Once"/> action finishes its single
    /// pass (or was requested on a pose the character lacks). Polled by the tick loop to resume
    /// autonomy. Clears the flag on read so the resume can't double-fire.</summary>
    public bool ActionJustCompleted()
    {
        if (!_actionCompleted) return false;
        _actionCompleted = false;
        return true;
    }

    /// <summary>Set the persistent base face expression (null restores neutral). When
    /// <paramref name="seconds"/> is given the expression auto-clears after that long.</summary>
    public void SetBaseExpression(string? name, double? seconds = null)
    {
        _baseExpression = name;
        _baseExpressionRemainingMs = name is not null && seconds is double s && s > 0 ? s * 1000.0 : -1;
    }

    /// <summary>Set the transient (drag) face expression, which overrides the base until cleared
    /// (null) — at which point the base expression shows through again.</summary>
    public void SetTransientExpression(string? name) => _transientExpression = name;

    /// <summary>Show <paramref name="name"/> as the base expression (null restores neutral). Kept as
    /// an alias of <see cref="SetBaseExpression(string?, double?)"/> for existing callers.</summary>
    public void SetExpression(string? name) => SetBaseExpression(name, null);

    /// <summary>Advance the animation by <paramref name="dt"/> seconds for the pet's current state.</summary>
    public void Update(CharacterSprites? sprites, PetState state, double dt)
    {
        // Auto-clear a timed base expression.
        if (_baseExpressionRemainingMs > 0)
        {
            _baseExpressionRemainingMs -= dt * 1000.0;
            if (_baseExpressionRemainingMs <= 0) { _baseExpression = null; _baseExpressionRemainingMs = -1; }
        }

        // A commanded action overrides the state-driven pose while it's set.
        string desired = _action ?? PoseFor(state);
        if (desired != _pose)
        {
            _pose = desired;
            _cycleIndex = 0;
            _elapsedMs = 0;
            FrameIndex = 0;
        }

        StepExpression(sprites, dt);

        var pose = sprites?.GetPose(_pose);
        if (pose is null || pose.Cycle.Count == 0 || pose.Frames.Count == 0)
        {
            // A commanded action pose the character doesn't have: finish it immediately so the caller
            // reverts instead of wedging on a missing pose.
            if (_action is not null) { _action = null; _actionCompleted = true; }
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
            // A Once action plays its cycle a single time, then reverts: when the last cycle frame's
            // delay elapses, flag completion and clear the action (next tick falls back to the pose
            // for the current state). Hold and locomotion poses keep looping.
            if (_action is not null && _actionMode == ActionMode.Once && _cycleIndex >= pose.Cycle.Count - 1)
            {
                _elapsedMs = 0;
                _action = null;
                _actionCompleted = true;
                break;
            }
            _elapsedMs -= delay;
            _cycleIndex = (_cycleIndex + 1) % pose.Cycle.Count;
            FrameIndex = Clamp(pose.Cycle[_cycleIndex], pose.Frames.Count);
            delay = FrameDelay(pose, FrameIndex);
        }
    }

    /// <summary>Loop the effective expression's frames by their authored delays (mirrors the pose
    /// loop), restarting whenever the effective expression changes. No-op when none is set or the
    /// character doesn't define it.</summary>
    private void StepExpression(CharacterSprites? sprites, double dt)
    {
        string? effective = Expression;
        if (effective != _activeExpr)
        {
            _activeExpr = effective;
            _exprCycleIndex = 0;
            _exprElapsedMs = 0;
            ExpressionFrameIndex = 0;
        }

        var expr = effective is null ? null : sprites?.GetExpression(effective);
        if (expr is null || expr.Frames.Count == 0)
        {
            ExpressionFrameIndex = 0;
            return;
        }

        _exprElapsedMs += dt * 1000.0;
        ExpressionFrameIndex = _exprCycleIndex % expr.Frames.Count;
        double delay = ExprDelay(expr, ExpressionFrameIndex);
        while (_exprElapsedMs >= delay)
        {
            _exprElapsedMs -= delay;
            _exprCycleIndex = (_exprCycleIndex + 1) % expr.Frames.Count;
            ExpressionFrameIndex = _exprCycleIndex;
            delay = ExprDelay(expr, ExpressionFrameIndex);
        }
    }

    private static double FrameDelay(CharacterSprites.Pose pose, int frame)
    {
        double d = pose.Frames[frame].DelayMs;
        return d > 0 ? d : 150; // guard against zero/negative authored delays
    }

    private static double ExprDelay(CharacterSprites.Expression expr, int frame)
    {
        double d = expr.Frames[frame].DelayMs;
        return d > 0 ? d : 100; // guard against zero/negative authored delays
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

    /// <summary>The non-locomotion poses the control API can command via <c>DoAction</c> (mapped from
    /// friendly names by <see cref="MaplePet.Engine.ActionRegistry"/>). Decoded for the live pet but
    /// deliberately excluded from the drag hit-test, so wide attack sprites don't inflate the grab box.</summary>
    public static readonly IReadOnlyCollection<string> ActionPoses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "prone", "sit", "alert", "heal", "fly", "proneStab", "swingO1", "swingO2", "swingO3", "stabO1", "stabO2" };

    /// <summary>Every pose the live pet should decode: the state-driven poses plus the commandable
    /// action poses. Passed as the load's <c>posesToLoad</c> while <see cref="ActivePoses"/> stays the
    /// hit-test set.</summary>
    public static readonly IReadOnlyCollection<string> LivePoses = BuildLivePoses();

    private static IReadOnlyCollection<string> BuildLivePoses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ActivePoses) set.Add(p);
        foreach (var p in ActionPoses) set.Add(p);
        return set;
    }
}
