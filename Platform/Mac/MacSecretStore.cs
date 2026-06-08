using System;
using System.Diagnostics;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Mac;

/// <summary>
/// macOS <see cref="ISecretStore"/> backed by the login Keychain via the <c>security</c> CLI. Each
/// secret is a generic-password item keyed by service <c>"TypePet"</c> + account = the id. Avoids a
/// Security.framework P/Invoke for the same reason the rest of the Mac layer shells out where it can.
/// Best-effort: a failed command degrades to "no secret".
/// </summary>
public sealed class MacSecretStore : ISecretStore
{
    private const string Service = "TypePet";

    public string? Get(string id)
    {
        var (ok, stdout) = Run("find-generic-password", "-s", Service, "-a", id, "-w");
        if (!ok) return null;
        var s = stdout.TrimEnd('\n', '\r');
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public void Set(string id, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) { Delete(id); return; }
        // -U updates the item in place if it already exists (otherwise add fails on a duplicate).
        Run("add-generic-password", "-s", Service, "-a", id, "-w", secret, "-U");
    }

    public void Delete(string id) => Run("delete-generic-password", "-s", Service, "-a", id);

    private static (bool ok, string stdout) Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.HasExited && p.ExitCode == 0, outp);
        }
        catch { return (false, ""); }
    }
}
