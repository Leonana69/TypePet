using System;

namespace MaplePet.Platform.Abstractions;

/// <summary>
/// Single-instance guard. The first process to acquire it becomes the owner and runs; a later process
/// finds it held, optionally pokes the owner so it can surface itself, and exits without starting a
/// second pet. Acquired before any window in Program.Main.
/// </summary>
public interface ISingleInstance : IDisposable
{
    /// <summary>True if this process is the first/owning instance and should run.</summary>
    bool IsOwner { get; }

    /// <summary>Raised on the OWNER (off the UI thread) each time another instance attempts to launch.
    /// Handlers must marshal to the UI thread themselves. May never fire on platforms without a
    /// cross-process signal (the guard still works; only the "I'm already here" poke is lost).</summary>
    event Action? Activated;

    /// <summary>(Second instance only) Wake the already-running owner so it can acknowledge the launch;
    /// this process should then exit. A no-op on the owner.</summary>
    void SignalOwner();
}
