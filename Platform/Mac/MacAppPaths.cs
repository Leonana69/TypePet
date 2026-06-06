using System;
using System.IO;
using MaplePet.Engine;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>
/// macOS on-disk locations. Inside a signed .app bundle, Contents/MacOS is effectively read-only, so
/// app data must live under <c>~/Library/Application Support/MaplePet</c>. When run from source
/// (<c>dotnet run</c>) it keeps the dev-friendly locations: settings next to the binary and characters
/// in the repo tree (same as Windows in dev).
/// </summary>
public sealed class MacAppPaths : IAppPaths
{
    public string DataRoot { get; }
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string CharactersRoot { get; }
    public string CommandsRoot { get; }

    public MacAppPaths()
    {
        if (IsInsideAppBundle())
        {
            DataRoot = Path.Combine(AppSupportRoot(), "MaplePet");
            CharactersRoot = Path.Combine(DataRoot, "Characters");
            CommandsRoot = Path.Combine(DataRoot, "Commands");
        }
        else
        {
            DataRoot = AppContext.BaseDirectory;
            CharactersRoot = CharacterStore.ResolveDefaultRoot();
            CommandsRoot = CommandStore.ResolveDefaultRoot();
        }
    }

    private static string AppSupportRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");

    private static bool IsInsideAppBundle()
    {
        var p = Environment.ProcessPath;
        return p is not null && p.Contains(".app/Contents/MacOS/", StringComparison.Ordinal);
    }
}
