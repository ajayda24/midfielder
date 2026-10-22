using Midfielder.Core.Feature;
using Xunit;

namespace Midfielder.Core.Tests;

public class PresenceTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_710_000_000);

    private static PresenceMonitor Monitor() => new() { NearRssi = -75, Delay = TimeSpan.FromSeconds(20) };

    /// <summary>Beacons once a second from <paramref name="from"/> for <paramref name="seconds"/>; ticks after each.</summary>
    private static bool Run(PresenceMonitor m, DateTimeOffset from, int seconds, int? rssi, bool blocked = false)
    {
        bool locked = false;
        for (int i = 0; i < seconds; i++)
        {
            var t = from.AddSeconds(i);
            if (rssi is { } r) m.Sample(r, t);
            locked |= m.Tick(t, blocked);
        }
        return locked;
    }

    [Fact]
    public void NeverLocksIfThePhoneWasNeverNear()
    {
        var m = Monitor();
        Assert.False(Run(m, T0, 60, rssi: -90));
        Assert.False(Run(m, T0.AddSeconds(60), 60, rssi: null));
        Assert.Equal(PresenceState.Waiting, m.State);
    }

    [Fact]
    public void LocksAfterTheDelayOnceThePhoneLeaves()
    {
        var m = Monitor();
        Assert.False(Run(m, T0, 10, rssi: -60));
        Assert.Equal(PresenceState.Near, m.State);
        // Gone: no beacons. Not before the delay...
        Assert.False(Run(m, T0.AddSeconds(10), 18, rssi: null));
        Assert.Equal(PresenceState.Leaving, m.State);
        // ...then it locks, exactly once.
        Assert.True(Run(m, T0.AddSeconds(28), 5, rssi: null));
        Assert.Equal(PresenceState.Locked, m.State);
        Assert.False(Run(m, T0.AddSeconds(33), 60, rssi: null));
    }

    [Fact]
    public void OneWeakReadingDoesNotCount()
    {
        var m = Monitor();
        Run(m, T0, 10, rssi: -60);
        m.Sample(-95, T0.AddSeconds(10)); // smoothed: still near
        Assert.True(m.IsNear);
        Assert.False(Run(m, T0.AddSeconds(11), 60, rssi: -60));
    }

    [Fact]
    public void NeverLocksWhileBlockedAndTheCountdownRestartsAfter()
    {
        var m = Monitor();
        Run(m, T0, 10, rssi: -60);
        Assert.False(Run(m, T0.AddSeconds(10), 120, rssi: null, blocked: true));
        // The call ended at +130: a full delay from then.
        Assert.False(Run(m, T0.AddSeconds(130), 15, rssi: null));
        Assert.True(Run(m, T0.AddSeconds(145), 10, rssi: null));
    }

    [Fact]
    public void ReArmsOnlyAfterUnlockAndNearAgain()
    {
        var m = Monitor();
        Run(m, T0, 10, rssi: -60);
        Assert.True(Run(m, T0.AddSeconds(10), 30, rssi: null));
        // Phone back while still locked: no new arming.
        Assert.False(Run(m, T0.AddSeconds(40), 5, rssi: -60));
        Assert.Equal(PresenceState.Locked, m.State);
        m.Unlocked();
        Assert.Equal(PresenceState.Waiting, m.State);
        Run(m, T0.AddSeconds(45), 5, rssi: -60);
        Assert.Equal(PresenceState.Near, m.State);
        Assert.True(Run(m, T0.AddSeconds(50), 30, rssi: null));
    }

    [Fact]
    public void BeaconCarryingTwoPcsTokensMatchesEach()
    {
        byte[] mine = new byte[32], other = new byte[32];
        mine[0] = 1;
        other[0] = 2;
        long slot = PresenceToken.Slot(T0);
        byte[] data = [PresenceToken.Version, .. PresenceToken.Compute(other, slot), .. PresenceToken.Compute(mine, slot)];
        Assert.True(PresenceToken.MatchesAny(mine, data, T0));
        Assert.True(PresenceToken.MatchesAny(other, data, T0));
        Assert.False(PresenceToken.MatchesAny(new byte[32], data, T0));
        Assert.False(PresenceToken.MatchesAny(mine, data.AsSpan(0, 12), T0)); // not a whole number of tokens
    }

    [Fact]
    public void TokenMatchesNeighbouringSlotsOnly()
    {
        var key = new byte[32];
        key[0] = 7;
        long slot = PresenceToken.Slot(T0);
        Assert.True(PresenceToken.Matches(key, PresenceToken.Compute(key, slot - 1), T0));
        Assert.True(PresenceToken.Matches(key, PresenceToken.Compute(key, slot + 1), T0));
        Assert.False(PresenceToken.Matches(key, PresenceToken.Compute(key, slot + 2), T0));
        var other = (byte[])key.Clone();
        other[1] = 1;
        Assert.False(PresenceToken.Matches(other, PresenceToken.Compute(key, slot), T0));
    }
}
