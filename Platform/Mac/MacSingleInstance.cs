using System;
using System.Threading;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>
/// macOS single-instance guard. .NET named <see cref="Mutex"/> is cross-process on macOS (it's
/// file-backed under the temp dir), so the same <c>createdNew</c> ownership test as Windows works. The
/// named EventWaitHandle used on Windows to poke the owner is unsupported on Unix, so the "I'm already
/// here" reactivation simply doesn't fire on macOS v1 — <see cref="Activated"/> stays silent, but the
/// guard (block a second pet) still works. Stays on JIT, not NativeAOT, where named mutexes have a known
/// cross-process bug.
/// </summary>
public sealed class MacSingleInstance : ISingleInstance
{
    // No "Local\" prefix: that's Windows kernel-namespace syntax; on Unix the name is a literal mapped
    // to a file, so keep it path-safe.
    private const string MutexName = "MaplePet.SingleInstance.A3F1C2E4-7B9D-4E6A-9C2F-1D8E5B0A4C71";

    private readonly Mutex? _mutex;

    public bool IsOwner { get; }
    public event Action? Activated { add { } remove { } } // no cross-process poke on macOS

    private MacSingleInstance(bool isOwner, Mutex? mutex)
    {
        IsOwner = isOwner;
        _mutex = mutex;
    }

    public static MacSingleInstance Acquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            return new MacSingleInstance(createdNew, mutex);
        }
        catch
        {
            // The OS denied the named object — fail open (a lone pet beats no pet).
            return new MacSingleInstance(isOwner: true, null);
        }
    }

    public void SignalOwner() { } // no-op on macOS

    public void Dispose() => _mutex?.Dispose();
}
