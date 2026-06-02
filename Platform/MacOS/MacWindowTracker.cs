using MaplePet.Engine;
using MaplePet.Platform;

namespace MaplePet.Platform.MacOS;

/// <summary>
/// FUTURE (see IMPLEMENTATION_PLAN.md section 10). On macOS this will use
/// CGWindowListCopyWindowInfo and map the menu bar / Dock into the same WorldGeometry.
/// Everything in Engine/, Rendering/, and Views/ is reused verbatim.
/// </summary>
public sealed class MacWindowTracker : IWindowTracker
{
    public WorldGeometry Capture() =>
        throw new PlatformNotSupportedException("macOS window tracker is not implemented yet.");
}
