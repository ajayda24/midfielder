namespace Midfielder.Core.Feature;

/// <summary>
/// Capability names for the optional features. A device advertises one only while the user has that feature switched
/// on, and sends feature messages only to peers that advertise it, so switching a feature off on either device stops
/// it for the pair.
/// </summary>
public static class Features
{
    /// <summary>Accepts phone notifications (NotificationPosted/Removed) and can act on them.</summary>
    public const string Notify = "notify";

    /// <summary>Accepts BatteryStatus.</summary>
    public const string Battery = "battery";

    /// <summary>Can be rung (RingRequest).</summary>
    public const string Ring = "ring";

    /// <summary>Accepts MediaState and MediaCommand.</summary>
    public const string Media = "media";

    /// <summary>Audio routes: AudioRequest, AudioOffer/AudioStop and audio streams.</summary>
    public const string Audio = "audio";
}
