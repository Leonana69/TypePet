using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Windows;

/// <summary>
/// Windows <see cref="ISecretStore"/> backed by DPAPI (<see cref="ProtectedData"/>, CurrentUser scope):
/// each secret is encrypted with the logged-in user's key and written as an opaque blob under
/// <c>&lt;DataRoot&gt;\secrets\&lt;id&gt;.bin</c>. The file is unreadable by other users and never contains
/// the key in plaintext. Best-effort: any IO/crypto failure degrades to "no secret" rather than throwing.
/// </summary>
public sealed class WindowsSecretStore : ISecretStore
{
    private readonly string _dir;

    public WindowsSecretStore(string dataRoot) => _dir = Path.Combine(dataRoot, "secrets");

    public string? Get(string id)
    {
        try
        {
            var path = PathFor(id);
            if (!File.Exists(path)) return null;
            var cipher = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public void Set(string id, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) { Delete(id); return; }
        try
        {
            Directory.CreateDirectory(_dir);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PathFor(id), cipher);
        }
        catch { /* best effort */ }
    }

    public void Delete(string id)
    {
        try { var p = PathFor(id); if (File.Exists(p)) File.Delete(p); }
        catch { /* best effort */ }
    }

    private string PathFor(string id) => Path.Combine(_dir, Sanitize(id) + ".bin");

    // Keep filenames to a safe subset; our ids are simple slugs, so this just guards against surprises.
    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
