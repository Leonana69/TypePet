using System;
using Microsoft.Win32;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Windows;

/// <summary>
/// "Start with Windows" support, backed by the per-user Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). The registry is the source of truth —
/// the Settings UI reads <see cref="IsEnabled"/> on open and calls <see cref="SetEnabled"/> on toggle.
/// All operations are best-effort and swallow failures (a locked-down machine just can't auto-start).
/// </summary>
public sealed class WindowsStartupAtLogin : IStartupAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TypePet";

    /// <summary>The Run key is always writable on Windows (best-effort), so this is always supported.</summary>
    public bool IsSupported => true;

    /// <summary>The executable to launch at login (the running host exe, quoted by the caller).</summary>
    private static string? ExePath => Environment.ProcessPath;

    /// <summary>True if TypePet is currently registered to start at login.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string v && !string.IsNullOrWhiteSpace(v);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Add or remove the Run-key entry. Returns false if the change couldn't be applied
    /// (e.g. the exe path is unknown or the registry write was blocked).</summary>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
            {
                var path = ExePath;
                if (string.IsNullOrWhiteSpace(path)) return false;
                key.SetValue(ValueName, "\"" + path + "\""); // quote in case the path has spaces
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
