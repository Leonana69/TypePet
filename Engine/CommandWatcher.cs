using System;
using System.IO;
using System.Timers;

namespace TypePet.Engine;

/// <summary>
/// Watches the user-commands root and fires a single debounced callback after the directory tree settles,
/// so the command registry hot-reloads when a folder is dropped in, edited, renamed, or removed — without
/// a restart. A burst of file events (an editor saving, a zip extracting) collapses into one
/// <c>onChanged</c> after a short quiet period.
///
/// The callback runs on a timer/thread-pool thread; the host marshals it onto the UI thread (the registry
/// rebuild touches UI-bound state). Best-effort: if the watcher can't start (path missing, IO error) it
/// stays inert rather than throwing, and the commands simply load once at startup.
/// </summary>
public sealed class CommandWatcher : IDisposable
{
    private const double DebounceMs = 400;

    private readonly FileSystemWatcher? _fsw;
    private readonly System.Timers.Timer _debounce;
    private readonly Action _onChanged;
    private bool _disposed;

    public CommandWatcher(string root, Action onChanged)
    {
        _onChanged = onChanged;
        _debounce = new System.Timers.Timer(DebounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => { try { if (!_disposed) _onChanged(); } catch { /* never crash the timer */ } };

        try
        {
            Directory.CreateDirectory(root);
            _fsw = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            _fsw.Created += OnEvent;
            _fsw.Changed += OnEvent;
            _fsw.Deleted += OnEvent;
            _fsw.Renamed += OnEvent;
            _fsw.EnableRaisingEvents = true;
        }
        catch
        {
            _fsw = null; // inert; startup load still happens
        }
    }

    private void OnEvent(object? sender, FileSystemEventArgs e) => Bump();
    private void OnEvent(object? sender, RenamedEventArgs e) => Bump();

    private void Bump()
    {
        if (_disposed) return;
        // Reset the quiet timer on every event so we fire once the burst settles.
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_fsw is not null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } } catch { }
        try { _debounce.Dispose(); } catch { }
    }
}
