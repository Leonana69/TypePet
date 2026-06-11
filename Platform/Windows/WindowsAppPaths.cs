using System;
using System.IO;
using TypePet.Engine;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Windows;

/// <summary>
/// Windows on-disk locations — fully portable, everything beside the binary: settings.json sits next
/// to the exe (<c>AppContext.BaseDirectory</c>), and characters/commands live under <c>Assets</c> in
/// the repo tree when run from source, else under <c>&lt;exe dir&gt;\Assets</c> (see
/// <see cref="CharacterStore.ResolveDefaultRoot"/>, which also migrates data left at the old
/// <c>%LOCALAPPDATA%\TypePet</c> location).
/// </summary>
public sealed class WindowsAppPaths : IAppPaths
{
    public string DataRoot => AppContext.BaseDirectory;
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string CharactersRoot => CharacterStore.ResolveDefaultRoot();
    public string CommandsRoot => CommandStore.ResolveDefaultRoot();
}
