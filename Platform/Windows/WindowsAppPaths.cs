using System;
using System.IO;
using MaplePet.Engine;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Windows;

/// <summary>
/// Windows on-disk locations — unchanged from the original behavior: settings.json sits next to the
/// binary (<c>AppContext.BaseDirectory</c>), and characters live in the repo tree when run from source,
/// else <c>%LOCALAPPDATA%\MaplePet\Characters</c> (see <see cref="CharacterStore.ResolveDefaultRoot"/>).
/// </summary>
public sealed class WindowsAppPaths : IAppPaths
{
    public string DataRoot => AppContext.BaseDirectory;
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string CharactersRoot => CharacterStore.ResolveDefaultRoot();
}
