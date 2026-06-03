using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MaplePet.Engine;
using MaplePet.Rendering;

namespace MaplePet.Api;

/// <summary>
/// The in-process implementation of <see cref="IPetControl"/>. It owns no runtime objects — it reads
/// them through accessor delegates so it always sees the live (swappable) <see cref="PetController"/>,
/// <see cref="CharacterAnimator"/> and <see cref="CharacterSprites"/>. Every command marshals onto the
/// UI thread (the only thread that touches pet/animator state), validates against the current
/// character's capabilities, enforces the autonomy-arbitration rules, logs, and returns a structured
/// <see cref="ControlResult"/>.
/// </summary>
public sealed class PetControlService : IPetControl
{
    /// <summary>The control-contract version, independent of the app/engine version. Bump only on a
    /// breaking change to the command surface.</summary>
    public const string ApiVersion = "1.0";

    private readonly Func<PetController?> _pet;
    private readonly Func<CharacterAnimator?> _animator;
    private readonly Func<CharacterSprites?> _sprites;
    private readonly Func<World?> _world;
    private readonly Func<MaplePet.Engine.Rect> _bounds;
    private readonly Func<(string id, string name)> _character;
    private readonly Action<string?, double?> _setSpeech;

    public event Action? CapabilitiesChanged;

    public PetControlService(
        Func<PetController?> pet, Func<CharacterAnimator?> animator, Func<CharacterSprites?> sprites,
        Func<World?> world, Func<MaplePet.Engine.Rect> bounds, Func<(string id, string name)> character,
        Action<string?, double?> setSpeech)
    {
        _pet = pet;
        _animator = animator;
        _sprites = sprites;
        _world = world;
        _bounds = bounds;
        _character = character;
        _setSpeech = setSpeech;
    }

    /// <summary>Raise <see cref="CapabilitiesChanged"/> (the worn character changed). Called by the
    /// owner after a live character swap.</summary>
    public void NotifyCapabilitiesChanged() => CapabilitiesChanged?.Invoke();

    // ---------------------------------------------------------------- Commands

    public Task<ControlResult> DoAction(string action, string? mode = null) => OnUi(() =>
    {
        var (pet, anim, sprites) = Live();
        if (pet is null || anim is null) return ControlResult.Fail("pet not ready");

        var def = ActionRegistry.Resolve(action);
        if (def is null)
            return Logged($"DoAction({action})", ControlResult.Reject($"unknown action '{action}'. Available: {ActionNames(sprites)}"));
        if (sprites?.GetPose(def.Pose) is null)
            return Logged($"DoAction({action})", ControlResult.Unsupported($"this character has no '{def.Name}' animation. Available: {ActionNames(sprites)}"));
        if (pet.IsDragging)
            return Logged($"DoAction({action})", ControlResult.Reject("can't act while being dragged"));
        if (def.RequiresGrounded && pet.State is not (PetState.Stand or PetState.Walk))
            return Logged($"DoAction({action})", ControlResult.Reject($"'{def.Name}' needs the pet on the ground (currently {pet.State})"));

        var m = ParseMode(mode, def.Mode);
        pet.SuspendRoaming();
        pet.StopAndIdle();
        anim.SetAction(def.Pose, m);
        return Logged($"DoAction({def.Name},{m})", ControlResult.Success($"playing {def.Name}"));
    });

    public Task<ControlResult> StopAction() => OnUi(() =>
    {
        var (pet, anim, _) = Live();
        anim?.SetAction(null);
        pet?.ResumeRoaming();
        return Logged("StopAction", ControlResult.Success());
    });

    public Task<ControlResult> Expression(string name, double? seconds = null) => OnUi(() =>
    {
        var (_, anim, sprites) = Live();
        if (anim is null) return ControlResult.Fail("pet not ready");
        if (sprites is not { HasExpressions: true })
            return Logged($"Expression({name})", ControlResult.Unsupported("this character has no facial expressions"));
        if (sprites.GetExpression(name) is null)
            return Logged($"Expression({name})", ControlResult.Reject($"unknown expression '{name}'. Available: {ExpressionNames(sprites)}"));
        anim.SetBaseExpression(name, seconds);
        return Logged($"Expression({name})", ControlResult.Success($"expression {name}"));
    });

    public Task<ControlResult> ClearExpression() => OnUi(() =>
    {
        _animator()?.SetBaseExpression(null, null);
        return Logged("ClearExpression", ControlResult.Success());
    });

    public Task<ControlResult> MoveTo(double x, double y) => OnUi(() =>
    {
        var (pet, anim, _) = Live();
        var world = _world();
        if (pet is null || world is null) return ControlResult.Fail("pet/world not ready");
        if (pet.IsDragging) return ControlResult.Reject("can't move while being dragged");

        anim?.SetAction(null);   // a move cancels any held action
        pet.ResumeRoaming();     // in Autonomous mode roaming resumes once the pet arrives
        if (!pet.RequestMoveTo(world, new Vec2(x, y)))
            return Logged($"MoveTo({x:0},{y:0})", ControlResult.Reject($"no reachable path to ({x:0}, {y:0})"));
        return Logged($"MoveTo({x:0},{y:0})", ControlResult.Success($"moving to ({x:0}, {y:0})"));
    });

    public Task<ControlResult> WalkTo(double x) => OnUi(() =>
    {
        var (pet, anim, _) = Live();
        var world = _world();
        if (pet is null || world is null) return ControlResult.Fail("pet/world not ready");
        if (pet.IsDragging) return ControlResult.Reject("can't move while being dragged");

        anim?.SetAction(null);
        pet.ResumeRoaming();
        if (!pet.RequestWalkTo(world, x))
            return Logged($"WalkTo({x:0})", ControlResult.Reject("the pet isn't on a surface; use MoveTo instead"));
        return Logged($"WalkTo({x:0})", ControlResult.Success($"walking to x={x:0}"));
    });

    public Task<ControlResult> Face(string dir) => OnUi(() =>
    {
        var pet = _pet();
        if (pet is null) return ControlResult.Fail("pet not ready");
        int d = dir?.Trim().ToLowerInvariant() switch { "left" => -1, "right" => 1, _ => 0 };
        if (d == 0) return ControlResult.Reject("dir must be 'left' or 'right'");
        pet.SetFacing(d);
        return Logged($"Face({dir})", ControlResult.Success($"facing {dir}"));
    });

    public Task<ControlResult> Stop() => OnUi(() =>
    {
        var (pet, anim, _) = Live();
        anim?.SetAction(null);
        pet?.StopAndIdle();
        pet?.ResumeRoaming();
        return Logged("Stop", ControlResult.Success());
    });

    public Task<ControlResult> AcquireControl() => OnUi(() =>
    {
        var pet = _pet();
        if (pet is null) return ControlResult.Fail("pet not ready");
        pet.AcquireControl();
        return Logged("AcquireControl", ControlResult.Success("manual control acquired"));
    });

    public Task<ControlResult> ReleaseControl() => OnUi(() =>
    {
        var (pet, anim, _) = Live();
        if (pet is null) return ControlResult.Fail("pet not ready");
        anim?.SetAction(null);
        pet.ReleaseControl();
        return Logged("ReleaseControl", ControlResult.Success("returned to autonomous mode"));
    });

    public Task<ControlResult> Say(string text, double? seconds = null) => OnUi(() =>
    {
        if (string.IsNullOrWhiteSpace(text)) return ControlResult.Reject("text is empty");
        double secs = seconds is double s && s > 0 ? s : DefaultSpeechSeconds(text);
        _setSpeech(text, secs);
        return Logged($"Say(\"{Truncate(text)}\")", ControlResult.Success());
    });

    // ---------------------------------------------------------------- Queries

    public Task<PetStatus> GetStatus() => OnUi(() =>
    {
        var (pet, anim, _) = Live();
        return new PetStatus(
            pet?.State.ToString() ?? "Unknown",
            anim?.Pose ?? "",
            anim?.Action,
            anim?.Expression,
            pet?.CenterX ?? 0, pet?.FeetY ?? 0,
            FacingStr(pet), pet?.IsDragging ?? false,
            pet?.Mode.ToString() ?? nameof(ControlMode.Autonomous));
    });

    public Task<CapabilitiesSnapshot> GetCapabilities() => OnUi(() =>
    {
        var (pet, anim, sprites) = Live();
        var (id, name) = _character();
        var b = _bounds();
        double roamMaxY = pet?.RoamMaxY ?? b.Bottom;
        if (!double.IsFinite(roamMaxY)) roamMaxY = b.Bottom;

        var actions = sprites is null
            ? new List<ActionInfo>()
            : ActionRegistry.All
                .Where(a => sprites.GetPose(a.Pose) is not null)
                .Select(a => new ActionInfo(a.Name, a.Mode == ActionMode.Hold, a.Description))
                .ToList();

        var expressions = sprites is null
            ? new List<ExpressionInfo>()
            : sprites.ExpressionNames
                .Where(n => !n.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(n => new ExpressionInfo(n))
                .ToList();

        return new CapabilitiesSnapshot(
            ApiVersion, AppInfo.Version, id, name,
            pet?.Mode.ToString() ?? nameof(ControlMode.Autonomous),
            BuildSnapshot(pet, anim),
            new ScreenBounds(b.Left, b.Top, b.Right, b.Bottom, roamMaxY),
            actions, expressions, SupportsSpeech: true);
    });

    // ---------------------------------------------------------------- Helpers

    /// <summary>Run <paramref name="f"/> on the UI thread (inline if already there) and return its
    /// result. A command body (T = <see cref="ControlResult"/>) that throws is converted to a failed
    /// result so the transport never sees a raw exception; query bodies rethrow as normal.</summary>
    private static async Task<T> OnUi<T>(Func<T> f)
    {
        T Safe()
        {
            try { return f(); }
            catch (Exception ex) when (typeof(T) == typeof(ControlResult))
            {
                return (T)(object)ControlResult.Fail(ex.Message);
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) return Safe();
        return await Dispatcher.UIThread.InvokeAsync(Safe);
    }

    private (PetController? pet, CharacterAnimator? anim, CharacterSprites? sprites) Live()
        => (_pet(), _animator(), _sprites());

    private PetSnapshot BuildSnapshot(PetController? pet, CharacterAnimator? anim)
    {
        if (pet is null)
            return new PetSnapshot("Unknown", anim?.Pose ?? "", anim?.Action, anim?.Expression,
                0, 0, 0, 0, 0, 0, "right", false, nameof(ControlMode.Autonomous));
        return new PetSnapshot(
            pet.State.ToString(), anim?.Pose ?? "", anim?.Action, anim?.Expression,
            pet.Pos.X, pet.Pos.Y, pet.CenterX, pet.FeetY, pet.Size.X, pet.Size.Y,
            FacingStr(pet), pet.IsDragging, pet.Mode.ToString());
    }

    private static string FacingStr(PetController? pet) => pet is null || pet.Facing >= 0 ? "right" : "left";

    private static string ActionNames(CharacterSprites? s) => s is null
        ? ""
        : string.Join(", ", ActionRegistry.All.Where(a => s.GetPose(a.Pose) is not null).Select(a => a.Name));

    private static string ExpressionNames(CharacterSprites? s) => s is null
        ? ""
        : string.Join(", ", s.ExpressionNames.Where(n => !n.Equals("default", StringComparison.OrdinalIgnoreCase)));

    private static ActionMode ParseMode(string? mode, ActionMode fallback) => mode?.Trim().ToLowerInvariant() switch
    {
        "once" => ActionMode.Once,
        "hold" or "loop" => ActionMode.Hold,
        _ => fallback,
    };

    private static double DefaultSpeechSeconds(string text) => Math.Clamp(2.0 + text.Length * 0.06, 2.0, 10.0);

    private static string Truncate(string t) => t.Length <= 40 ? t : t[..40] + "…";

    private static ControlResult Logged(string command, ControlResult result)
    {
        Trace.WriteLine($"[MaplePet.Control] {command} -> {result.Status}{(result.Reason is null ? "" : $": {result.Reason}")}");
        return result;
    }
}
