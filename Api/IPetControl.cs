using System;
using System.Threading.Tasks;

namespace MaplePet.Api;

/// <summary>
/// The programmatic control surface for the pet — the contract a future LLM connection (or the bundled
/// MCP tool server) targets. Every method is thread-safe: it marshals onto the UI thread internally,
/// so it can be called from any thread, and returns a structured result/snapshot safe to feed back to
/// an LLM. Expected refusals come back as <see cref="CommandStatus.Rejected"/>/<see cref="CommandStatus.Unsupported"/>
/// rather than thrown exceptions.
///
/// This is the stable transport seam: an MCP server / HTTP endpoint / stdio bridge wraps this
/// interface without the core needing to change.
/// </summary>
public interface IPetControl
{
    /// <summary>The full vocabulary + live state + coordinate bounds for the current character.
    /// Call this first; it's the source of truth for what actions/expressions are valid.</summary>
    Task<CapabilitiesSnapshot> GetCapabilities();

    /// <summary>A lightweight snapshot of the pet's current state.</summary>
    Task<PetStatus> GetStatus();

    /// <summary>Play an action animation (e.g. "prone", "alert", "attack"). <paramref name="mode"/>
    /// is "once" or "hold"; omit for the action's default.</summary>
    Task<ControlResult> DoAction(string action, string? mode = null);

    /// <summary>Clear a held action and return to idle.</summary>
    Task<ControlResult> StopAction();

    /// <summary>Set the face expression (e.g. "angry", "smile"); optionally auto-clear after
    /// <paramref name="seconds"/>.</summary>
    Task<ControlResult> Expression(string name, double? seconds = null);

    /// <summary>Restore the neutral face.</summary>
    Task<ControlResult> ClearExpression();

    /// <summary>Walk/navigate the pet to a screen point (logical px), using pathfinding.</summary>
    Task<ControlResult> MoveTo(double x, double y);

    /// <summary>Walk the pet to a horizontal position at its current height.</summary>
    Task<ControlResult> WalkTo(double x);

    /// <summary>Face "left" or "right".</summary>
    Task<ControlResult> Face(string dir);

    /// <summary>Halt motion, clear any held action, and idle in place.</summary>
    Task<ControlResult> Stop();

    /// <summary>Take exclusive (manual) control — freeze autonomous roaming until released.</summary>
    Task<ControlResult> AcquireControl();

    /// <summary>Return the pet to autonomous behaviour.</summary>
    Task<ControlResult> ReleaseControl();

    /// <summary>Show a speech bubble over the pet for <paramref name="seconds"/> (default scales with
    /// text length). Optionally <paramref name="linkUrl"/> + <paramref name="linkLabel"/> add a clickable
    /// link line to the bubble that opens the URL in the default browser (used by the <c>/rank</c> command).</summary>
    Task<ControlResult> Say(string text, double? seconds = null, string? linkUrl = null, string? linkLabel = null);

    /// <summary>Raised when the worn character changes, so a transport can regenerate its tool schema
    /// (the available actions/expressions are character-specific).</summary>
    event Action? CapabilitiesChanged;
}
