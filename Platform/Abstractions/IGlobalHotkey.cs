using System;

namespace MaplePet.Platform.Abstractions;

/// <summary>
/// A system-wide hotkey that pops up the say-input bar from anywhere. Windows uses a low-level keyboard
/// hook (WH_KEYBOARD_LL) that can swallow the chord; macOS uses Carbon RegisterEventHotKey (permission
/// free). A platform with no implementation reports <see cref="IsSupported"/> = false and no-ops.
/// </summary>
public interface IGlobalHotkey : IDisposable
{
    /// <summary>False on platforms that can't register a global hotkey; PetWindow then skips installing
    /// it (the pet can still be double-clicked to open the say bar). Never throws.</summary>
    bool IsSupported { get; }

    /// <summary>Raised on the UI thread when the configured chord is pressed.</summary>
    event Action? Triggered;

    /// <summary>Install the global listener. Call once on the UI thread.</summary>
    void Install();

    /// <summary>Set the active chord (a virtual-key code + required modifiers). A non-positive
    /// <paramref name="virtualKey"/> disables matching without uninstalling.</summary>
    void SetHotkey(int virtualKey, bool ctrl, bool alt, bool shift, bool meta);

    /// <summary>Stop matching the hotkey (the listener stays installed but inert).</summary>
    void Disable();
}
