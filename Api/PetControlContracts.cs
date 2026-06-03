namespace MaplePet.Api;

/// <summary>The outcome category of a control command. <see cref="Rejected"/> = a valid request the
/// pet can't honour right now (e.g. acting while dragged); <see cref="Unsupported"/> = the current
/// character lacks the capability; <see cref="Error"/> = an unexpected failure.</summary>
public enum CommandStatus { Ok, Rejected, Unsupported, Error }

/// <summary>The uniform result of every command — small and self-describing, so it can be fed
/// straight back to an LLM. <see cref="Reason"/> explains a non-Ok status (and lists valid options on
/// a vocabulary mismatch); <see cref="Detail"/> is an optional note on success.</summary>
public sealed record ControlResult(CommandStatus Status, string? Reason = null, string? Detail = null)
{
    public bool Ok => Status == CommandStatus.Ok;

    public static ControlResult Success(string? detail = null) => new(CommandStatus.Ok, null, detail);
    public static ControlResult Reject(string reason) => new(CommandStatus.Rejected, reason);
    public static ControlResult Unsupported(string reason) => new(CommandStatus.Unsupported, reason);
    public static ControlResult Fail(string reason) => new(CommandStatus.Error, reason);
}

/// <summary>A commandable action available to the current character. <see cref="Loops"/> is true for
/// hold actions (the pet keeps the pose until cleared/moved) and false for one-shot gestures.</summary>
public sealed record ActionInfo(string Name, bool Loops, string Description);

/// <summary>A face expression available to the current character.</summary>
public sealed record ExpressionInfo(string Name);

/// <summary>The coordinate space (logical px) movement commands operate in: the overlay rectangle,
/// plus <see cref="RoamMaxY"/> — the lowest feet-Y the pet stays above so it isn't drawn off-screen.</summary>
public sealed record ScreenBounds(double MinX, double MinY, double MaxX, double MaxY, double RoamMaxY);

/// <summary>A live snapshot of the pet's runtime state. Positions are logical px;
/// <see cref="Facing"/> is "left"/"right"; <see cref="Mode"/> is "Autonomous"/"Manual".</summary>
public sealed record PetSnapshot(
    string State, string Pose, string? Action, string? Expression,
    double X, double Y, double CenterX, double FeetY, double Width, double Height,
    string Facing, bool IsDragging, string Mode);

/// <summary>A lightweight live-state record (a trimmed <see cref="PetSnapshot"/>) for quick polls.</summary>
public sealed record PetStatus(
    string State, string Pose, string? Action, string? Expression,
    double CenterX, double FeetY, string Facing, bool IsDragging, string Mode);

/// <summary>Everything an LLM needs to drive the pet: API/engine versions, the worn character, live
/// state, the coordinate bounds, and the actions/expressions THIS character supports. This is the
/// single source of truth a transport builds its tool schema (enum values, ranges) from.</summary>
public sealed record CapabilitiesSnapshot(
    string ApiVersion, string EngineVersion,
    string CharacterId, string CharacterName, string Mode,
    PetSnapshot Pet, ScreenBounds Bounds,
    IReadOnlyList<ActionInfo> Actions, IReadOnlyList<ExpressionInfo> Expressions,
    bool SupportsSpeech);
