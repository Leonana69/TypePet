using System;
using System.Threading;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Windows;

/// <summary>
/// Single-instance guard. The first process to call <see cref="Acquire"/> creates and owns a named
/// system mutex and runs normally; any later process finds the mutex already held, pokes the owner
/// through a shared named event (so it can surface itself) and exits without starting a second overlay.
///
/// Ownership is decided purely by the mutex's <c>createdNew</c> flag, so a crashed owner leaves no
/// stale lock — the OS closes its handle, the kernel object vanishes, and the next launch becomes the
/// owner. The names live in the per-session (<c>Local\</c>) kernel namespace, so each interactive login
/// session (console, RDP, fast-user-switch) gets its own pet; only duplicate launches within one
/// session are blocked. Every step is best-effort: if the OS denies the named objects the app still
/// starts (one extra pet beats no pet).
/// </summary>
public sealed class WindowsSingleInstance : ISingleInstance
{
    // A fixed GUID keeps these names from colliding with any other app's kernel objects; the
    // "Local\" prefix scopes them to the current login session.
    private const string MutexName  = @"Local\TypePet.SingleInstance.A3F1C2E4-7B9D-4E6A-9C2F-1D8E5B0A4C71";
    private const string SignalName = @"Local\TypePet.Activate.A3F1C2E4-7B9D-4E6A-9C2F-1D8E5B0A4C71";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _signal;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    /// <summary>True if this process is the first/owning instance and should run.</summary>
    public bool IsOwner { get; }

    /// <summary>The owning guard for this process, set by <see cref="Acquire"/>. The app reads this to
    /// subscribe to <see cref="Activated"/>; it is only ever the owner's instance (a second process
    /// exits immediately after <see cref="Acquire"/>).</summary>
    public static WindowsSingleInstance? Current { get; private set; }

    /// <summary>Raised (on a thread-pool thread) on the OWNER each time another instance attempts to
    /// launch. Handlers must marshal to the UI thread themselves.</summary>
    public event Action? Activated;

    private WindowsSingleInstance(bool isOwner, Mutex? mutex, EventWaitHandle? signal)
    {
        IsOwner = isOwner;
        _mutex = mutex;
        _signal = signal;

        // Only the owner listens. An auto-reset event plus a repeating registration means every later
        // launch attempt wakes us, not just the first.
        if (isOwner && signal is not null)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(
                signal, (_, _) => Activated?.Invoke(), state: null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// <summary>Acquire the guard for this process. The returned instance's <see cref="IsOwner"/> tells
    /// the caller whether to run (true) or defer to the already-running instance (false). Never throws.</summary>
    public static WindowsSingleInstance Acquire()
    {
        Mutex? mutex = null;
        bool isOwner;
        try
        {
            // createdNew == true  -> we made the mutex, so we're the first/owning instance.
            // createdNew == false -> it already existed, so another instance is live.
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            isOwner = createdNew;
        }
        catch
        {
            // The kernel object couldn't be created (locked-down policy, name clash, …). Fail open:
            // run as a lone instance rather than refusing to start.
            mutex?.Dispose();
            return Current = new WindowsSingleInstance(isOwner: true, null, null);
        }

        // The activation signal is a nicety that lets a second launch poke the owner. If it can't be
        // created, single-instance detection still works — we just won't surface the running pet.
        EventWaitHandle? signal;
        try { signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName, out _); }
        catch { signal = null; }

        return Current = new WindowsSingleInstance(isOwner, mutex, signal);
    }

    /// <summary>(Second instance only) Wake the already-running owner so it can acknowledge the launch;
    /// this process should then exit. A no-op on the owner.</summary>
    public void SignalOwner()
    {
        if (IsOwner) return;
        try { _signal?.Set(); } catch { /* best-effort: a missed poke just means no acknowledgement */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Tear the wait registration down BEFORE closing the event it waits on. Unregister(done) signals
        // `done` once any in-flight callback has finished, so waiting on it guarantees no thread-pool
        // thread is still touching _signal when we dispose it. (Dispose runs only at process exit, and
        // the callback just posts to the UI thread and returns, so this wait is effectively instant.)
        if (_registration is not null)
        {
            using var done = new ManualResetEvent(false);
            if (_registration.Unregister(done)) done.WaitOne();
            _registration = null;
        }

        _signal?.Dispose();
        _mutex?.Dispose(); // closes the handle, releasing the kernel mutex so the next launch can own it
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
