using TypePet.Engine;

namespace TypePet.Platform.Abstractions;

/// <summary>
/// The seam between the shared engine and OS-specific window enumeration. Returns the current
/// visible window rectangles and the taskbar/ground, in the platform's screen-coordinate unit
/// (Windows: physical pixels; macOS: points — see <c>ScreenSpace</c> for how the overlay reconciles
/// the unit). Called a few times per second (worldPollHz), never every frame.
/// </summary>
public interface IWindowTracker
{
    WorldGeometry Capture();

    /// <summary>
    /// True when the foreground window is a borderless / exclusive-fullscreen app that covers its
    /// entire monitor (the taskbar included). A normal window that's merely maximized is NOT
    /// fullscreen. Used to hide the pet so it doesn't sit on top of (or kick out of fullscreen) a game
    /// or video. Returns false on platforms without a real implementation.
    /// </summary>
    bool IsForegroundFullscreen();
}
