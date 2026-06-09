using System.Diagnostics;
using Avalonia.Threading;

namespace TypePet.Engine;

/// <summary>
/// A fixed-cadence tick scheduler built on Avalonia's <see cref="DispatcherTimer"/>
/// (Avalonia has no built-in game loop). Raises <see cref="Tick"/> with the real
/// elapsed seconds since the previous tick, clamped to avoid huge steps after a pause.
/// </summary>
public sealed class GameLoop
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private TimeSpan _last;

    /// <summary>Fired each frame. Argument is delta-time in seconds.</summary>
    public event Action<double>? Tick;

    public GameLoop(int fps)
    {
        int hz = Math.Clamp(fps, 1, 240);
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / hz),
        };
        _timer.Tick += OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double dt = (now - _last).TotalSeconds;
        _last = now;
        if (dt <= 0) return;
        if (dt > 0.1) dt = 0.1; // clamp after long stalls so physics stays stable
        Tick?.Invoke(dt);
    }

    public void Start()
    {
        _clock.Restart();
        _last = TimeSpan.Zero;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
