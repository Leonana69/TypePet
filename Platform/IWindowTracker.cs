using MaplePet.Engine;

namespace MaplePet.Platform;

/// <summary>
/// The single seam between the shared engine and OS-specific code. Returns the current
/// visible window rectangles and the taskbar, all in PHYSICAL screen pixels. Called a
/// few times per second (worldPollHz), never every frame.
/// </summary>
public interface IWindowTracker
{
    WorldGeometry Capture();
}
