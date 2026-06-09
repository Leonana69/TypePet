using System.Reflection;

namespace TypePet;

/// <summary>
/// App-level identity read from the assembly metadata baked in by TypePet.csproj. The version lives
/// there (the <c>&lt;Version&gt;</c> property) as the single source of truth; this just surfaces it to
/// the UI (the Settings "About" row, the tray tooltip) so the number is never duplicated in code.
/// </summary>
public static class AppInfo
{
    public const string Name = "TypePet";

    /// <summary>The semantic version, e.g. <c>"1.0.0"</c>, read from the assembly's
    /// InformationalVersion (any <c>"+buildmetadata"</c> suffix the SDK may append is trimmed off).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary><c>"TypePet 1.0.0"</c> — for window titles / tooltips.</summary>
    public static string NameAndVersion { get; } = $"{Name} {Version}";

    private static string ReadVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+'); // strip a "+<commit>" build-metadata suffix if present
            return plus >= 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
