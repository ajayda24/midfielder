package dev.midfielder.core.feature

/**
 * Capability names for the optional features. A device advertises one only while the user has that feature
 * switched on, and sends feature messages only to peers that advertise it, so switching a feature off on either
 * device stops it for the pair.
 */
public object Features {
    /** Accepts phone notifications (NotificationPosted/Removed) and can act on them. */
    public const val NOTIFY: String = "notify"

    /** Accepts BatteryStatus. */
    public const val BATTERY: String = "battery"

    /** Can be rung (RingRequest). */
    public const val RING: String = "ring"

    /** Accepts MediaState and MediaCommand. */
    public const val MEDIA: String = "media"

    /** Audio routes: AudioRequest, AudioOffer/AudioStop and audio streams. */
    public const val AUDIO: String = "audio"
}
