namespace GameNet.Core;

/// <summary>
/// Client-side tick clock. Advances with wall time and snaps to the server tick when the drift
/// exceeds the configured tolerance (server sends TickSync every N ticks).
/// </summary>
public sealed class TickClock
{
    private readonly object _gate = new();
    private double _accumulator;

    public TickClock(double tickRate) => TickRate = tickRate > 0 ? tickRate : 30;

    public double TickRate { get; }
    public uint CurrentTick { get; private set; }
    public uint ServerTick { get; private set; }
    public int LastDrift { get; private set; }
    public int LastCorrection { get; private set; }
    public long SyncCount { get; private set; }

    public void Advance(double seconds)
    {
        if (seconds <= 0) return;
        lock (_gate)
        {
            _accumulator += seconds * TickRate;
            var whole = (int)_accumulator;
            if (whole <= 0) return;
            CurrentTick = CurrentTick + (uint)whole;
            _accumulator -= whole;
        }
    }

    /// <summary>Applies an authoritative tick and returns the pre-correction drift.</summary>
    public int Synchronize(uint serverTick, int tolerance)
    {
        lock (_gate)
        {
            ServerTick = serverTick;
            LastDrift = (int)(CurrentTick - serverTick);
            LastCorrection = 0;
            if (Math.Abs(LastDrift) > tolerance)
            {
                LastCorrection = -LastDrift;
                CurrentTick = serverTick;
                _accumulator = 0;
            }
            SyncCount++;
            return LastDrift;
        }
    }
}
