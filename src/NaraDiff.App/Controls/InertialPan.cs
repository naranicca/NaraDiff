using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace NaraDiff.App.Controls;

/// <summary>Continues a completed pen drag and slows it smoothly to a stop.</summary>
internal sealed class InertialPan
{
    private const double MinimumSpeed = 0.02;
    private const double deceleration = 0.0025;
    private readonly Action<Vector> _move;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Vector _velocity;
    private TimeSpan _lastTick;

    public InertialPan(Action<Vector> move)
    {
        _move = move ?? throw new ArgumentNullException(nameof(move));
        _timer.Tick += (_, _) => Tick();
    }

    public void Start(Vector velocity)
    {
        _velocity = velocity;
        if (_velocity.Length < MinimumSpeed) return;
        _lastTick = _clock.Elapsed;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        var now = _clock.Elapsed;
        var elapsedMilliseconds = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;
        if (elapsedMilliseconds <= 0) return;
        _move(_velocity * elapsedMilliseconds);
        var speed = _velocity.Length;
        var nextSpeed = Math.Max(0, speed - deceleration * elapsedMilliseconds);
        if (nextSpeed < MinimumSpeed)
        {
            Stop();
            return;
        }
        _velocity *= nextSpeed / speed;
    }
}