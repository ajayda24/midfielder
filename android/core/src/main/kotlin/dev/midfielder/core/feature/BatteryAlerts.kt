package dev.midfielder.core.feature

/**
 * Decides when to alert about a battery: once when charging reaches [fullAt] ("unplug to protect the battery") and
 * once when it drops to [lowAt] unplugged. Each alert re-arms only after the level moves well away (5 points) or the
 * charger state flips, so a battery hovering at 79-80% doesn't alert over and over.
 */
public class BatteryAlerts(fullAt: Int = 80, public var lowAt: Int = 15) {
    public enum class Alert { FULL, LOW }

    private var fullFired = false
    private var lowFired = false

    /** Changing the level re-arms the alert (an 80% alert that fired must not keep the new 90% one silent). */
    public var fullAt: Int = fullAt
        set(v) {
            if (v != field) fullFired = false
            field = v
        }

    public fun update(level: Int, charging: Boolean): Alert? {
        if (!charging || level < fullAt - REARM) fullFired = false
        if (charging || level > lowAt + REARM) lowFired = false
        if (charging && level >= fullAt && !fullFired) {
            fullFired = true
            return Alert.FULL
        }
        if (!charging && level <= lowAt && !lowFired) {
            lowFired = true
            return Alert.LOW
        }
        return null
    }

    private companion object {
        const val REARM = 5
    }
}
