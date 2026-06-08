namespace TypePet.Platform.Abstractions;

/// <summary>
/// "Start at login" support. Windows: the per-user Run registry key. macOS: SMAppService.mainApp (needs
/// a real .app bundle). All operations are best-effort and never throw; a locked-down or unbundled
/// environment just can't auto-start, in which case <see cref="IsSupported"/> is false and the Settings
/// toggle is disabled.
/// </summary>
public interface IStartupAtLogin
{
    /// <summary>False when launch-at-login can't be registered in the current environment (e.g. macOS
    /// running outside a .app bundle). The Settings row is disabled when false.</summary>
    bool IsSupported { get; }

    /// <summary>True if the app is currently registered to start at login.</summary>
    bool IsEnabled();

    /// <summary>Add or remove the login registration. Returns false if the change couldn't be applied.</summary>
    bool SetEnabled(bool enabled);
}
