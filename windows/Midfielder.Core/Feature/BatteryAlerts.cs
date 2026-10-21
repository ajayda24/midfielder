namespace Midfielder.Core.Feature;

public enum BatteryAlert
{
    Full,
    Low,
}

/// <summary>
/// Decides when to alert about a battery: once when charging reaches <see cref="FullAt"/> ("unplug to protect the
/// battery") and once when it drops to <see cref="LowAt"/> unplugged. Each alert re-arms only after the level moves
/// well away (5 points) or the charger state flips, so a battery hovering at 79-80% doesn't alert over and over.
/// </summary>
public sealed class BatteryAlerts(int fullAt = 80, int lowAt = 15)
{
    private const int Rearm = 5;
    private bool _fullFired;
    private bool _lowFired;

    public int FullAt { get; set; } = fullAt;
    public int LowAt { get; set; } = lowAt;

    public BatteryAlert? Update(int level, bool charging)
    {
        if (!charging || level < FullAt - Rearm) _fullFired = false;
        if (charging || level > LowAt + Rearm) _lowFired = false;
        if (charging && level >= FullAt && !_fullFired)
        {
            _fullFired = true;
            return BatteryAlert.Full;
        }
        if (!charging && level <= LowAt && !_lowFired)
        {
            _lowFired = true;
            return BatteryAlert.Low;
        }
        return null;
    }
}
