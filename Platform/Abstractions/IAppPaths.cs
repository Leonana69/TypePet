namespace MaplePet.Platform.Abstractions;

/// <summary>
/// Resolves writable on-disk locations per platform. Matters because a signed macOS .app bundle's
/// Contents/MacOS directory is effectively read-only, so settings / the character store / the
/// single-instance lock must live under ~/Library/Application Support/MaplePet instead of next to the
/// binary. Windows keeps its existing locations (settings beside the binary; characters in the repo
/// tree when run from source, else %LOCALAPPDATA%).
/// </summary>
public interface IAppPaths
{
    /// <summary>A writable per-user directory for app data (settings, the single-instance lock).</summary>
    string DataRoot { get; }

    /// <summary>Full path to settings.json.</summary>
    string SettingsPath { get; }

    /// <summary>Root directory for the user's imported characters.</summary>
    string CharactersRoot { get; }

    /// <summary>Root directory for the user's installed commands (the hot-reloaded skill library).</summary>
    string CommandsRoot { get; }
}
