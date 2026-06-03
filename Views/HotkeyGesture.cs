using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace MaplePet.Views;

/// <summary>
/// Parses/formats the say-input hotkey as a compact <c>"Ctrl+Alt+Space"</c> string and maps the
/// chord's main key to its Win32 virtual-key code (what <c>HotkeyListener</c>'s keyboard hook compares
/// against). Deliberately self-contained — no Avalonia <see cref="KeyGesture"/> round-trip — so the
/// on-disk string stays stable and human-editable. Supported main keys are letters, digits, F1–F24 and
/// Space: enough for a global shortcut, and the only ones we can map to a VK without a full table.
/// </summary>
internal static class HotkeyGesture
{
    /// <summary>Parse a gesture string into its modifiers + main key. Returns false on anything
    /// malformed, or a key we can't map to a virtual-key code.</summary>
    public static bool TryParse(string? gesture, out KeyModifiers modifiers, out Key key)
    {
        modifiers = KeyModifiers.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= KeyModifiers.Control; break;
                case "alt": modifiers |= KeyModifiers.Alt; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "win": case "meta": case "cmd": modifiers |= KeyModifiers.Meta; break;
                default:
                    if (key != Key.None) return false; // two non-modifier tokens — malformed
                    if (!TryParseKey(raw, out key)) return false;
                    break;
            }
        }
        return key != Key.None && KeyToVirtualKey(key) is not null;
    }

    /// <summary>Format modifiers + key back into the canonical <c>"Ctrl+Alt+Space"</c> string.</summary>
    public static string Format(KeyModifiers modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>The Win32 virtual-key code for a supported main key, or null if unsupported. Each range
    /// is contiguous in both the Avalonia <see cref="Key"/> enum and the VK numbering, so an offset from
    /// the range start maps one to the other regardless of the enum's absolute values.</summary>
    public static int? KeyToVirtualKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return 0x41 + (key - Key.A);     // 'A'..'Z'
        if (key >= Key.D0 && key <= Key.D9) return 0x30 + (key - Key.D0);   // '0'..'9'
        if (key >= Key.F1 && key <= Key.F24) return 0x70 + (key - Key.F1);  // VK_F1..VK_F24
        if (key == Key.Space) return 0x20;                                  // VK_SPACE
        return null;
    }

    /// <summary>True if the gesture parses to a mappable key AND carries a strong modifier
    /// (Ctrl/Alt/Win) — i.e. it's safe to register as a global hotkey, never a bare key. The single
    /// source of the "bindable" rule, shared by the settings UI (display) and the registration site.</summary>
    public static bool IsBindable(string? gesture)
        => TryParse(gesture, out var mods, out _)
           && (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta));

    /// <summary>True for the modifier keys themselves, so an in-progress capture ignores them.</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c >= 'A' && c <= 'Z') { key = Key.A + (c - 'A'); return true; }
            if (c >= '0' && c <= '9') { key = Key.D0 + (c - '0'); return true; }
        }
        if (string.Equals(token, "Space", StringComparison.OrdinalIgnoreCase)) { key = Key.Space; return true; }
        // F-keys and other named keys — but reject a bare number, which Enum.TryParse would otherwise
        // read as the raw underlying enum value.
        if (!char.IsDigit(token[0]) && Enum.TryParse(token, ignoreCase: true, out Key parsed))
        {
            key = parsed;
            return true;
        }
        return false;
    }

    private static string KeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        return key.ToString();
    }
}
