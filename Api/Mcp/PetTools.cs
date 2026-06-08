using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace MaplePet.Api.Mcp;

/// <summary>
/// The MCP tool surface: one tool per <see cref="IPetControl"/> command. The <see cref="IPetControl"/>
/// instance is resolved from DI (registered as a singleton in <see cref="PetMcpServer"/>); the
/// remaining parameters come from the tool-call arguments. Each tool returns the facade's structured
/// result/snapshot, which the SDK serializes back to the client. An LLM should call
/// <c>capabilities</c> first to learn the valid action/expression names for the worn character.
/// </summary>
[McpServerToolType]
public sealed class PetTools
{
    [McpServerTool(Name = "capabilities")]
    [Description("List the actions and expressions the current character supports, plus live pet state, control mode, and screen bounds. Call this first to learn valid action/expression names and coordinate ranges.")]
    public static Task<CapabilitiesSnapshot> Capabilities(IPetControl pet) => pet.GetCapabilities();

    [McpServerTool(Name = "describe")]
    [Description("Get the pet's current state: pose, active action, expression, position, facing, dragging, and control mode.")]
    public static Task<PetStatus> Describe(IPetControl pet) => pet.GetStatus();

    [McpServerTool(Name = "do_action")]
    [Description("Play an action animation on the pet (e.g. prone, sit, alert, heal, jump, fly, attack, stab, swing, shoot). 'jump' hops onto a platform directly above within jump height (else hops in place); 'fly' glides up onto the platform above at any height (else floats up a little and back). 'attack' randomly stabs/swings/shoots with whatever the character carries, then stays alert briefly. Use a name from 'capabilities'.")]
    public static Task<ControlResult> DoAction(
        IPetControl pet,
        [Description("Action name from capabilities, e.g. 'prone' or 'attack'.")] string action,
        [Description("'once' (play once) or 'hold' (stay until cleared); omit for the action's default. Ignored for attack actions.")] string? mode = null)
        => pet.DoAction(action, mode);

    [McpServerTool(Name = "stop_action")]
    [Description("Clear a held action and return the pet to idle.")]
    public static Task<ControlResult> StopAction(IPetControl pet) => pet.StopAction();

    [McpServerTool(Name = "set_expression")]
    [Description("Set the pet's facial expression (e.g. angry, smile, cry, love). Use a name from 'capabilities'.")]
    public static Task<ControlResult> SetExpression(
        IPetControl pet,
        [Description("Expression name from capabilities.")] string name,
        [Description("Optional: auto-clear after this many seconds.")] double? seconds = null)
        => pet.Expression(name, seconds);

    [McpServerTool(Name = "clear_expression")]
    [Description("Restore the pet's neutral face.")]
    public static Task<ControlResult> ClearExpression(IPetControl pet) => pet.ClearExpression();

    [McpServerTool(Name = "move_to")]
    [Description("Walk/navigate the pet to a screen point (logical px). Use coordinate ranges from 'capabilities' bounds.")]
    public static Task<ControlResult> MoveTo(
        IPetControl pet,
        [Description("Target x in logical pixels.")] double x,
        [Description("Target y in logical pixels.")] double y)
        => pet.MoveTo(x, y);

    [McpServerTool(Name = "walk_to")]
    [Description("Walk the pet horizontally to an x position at its current height.")]
    public static Task<ControlResult> WalkTo(
        IPetControl pet,
        [Description("Target x in logical pixels.")] double x)
        => pet.WalkTo(x);

    [McpServerTool(Name = "face")]
    [Description("Make the pet face a direction.")]
    public static Task<ControlResult> Face(
        IPetControl pet,
        [Description("'left' or 'right'.")] string dir)
        => pet.Face(dir);

    [McpServerTool(Name = "stop")]
    [Description("Halt the pet's motion, clear any held action, and idle in place.")]
    public static Task<ControlResult> Stop(IPetControl pet) => pet.Stop();

    [McpServerTool(Name = "acquire_control")]
    [Description("Take exclusive manual control: freeze the pet's autonomous roaming until released.")]
    public static Task<ControlResult> AcquireControl(IPetControl pet) => pet.AcquireControl();

    [McpServerTool(Name = "release_control")]
    [Description("Return the pet to autonomous behaviour (resume roaming).")]
    public static Task<ControlResult> ReleaseControl(IPetControl pet) => pet.ReleaseControl();

    [McpServerTool(Name = "say")]
    [Description("Show a speech bubble above the pet.")]
    public static Task<ControlResult> Say(
        IPetControl pet,
        [Description("The text to display.")] string text,
        [Description("Optional: how long to show it, in seconds (default scales with length).")] double? seconds = null)
        => pet.Say(text, seconds);
}
