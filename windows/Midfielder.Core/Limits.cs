namespace Midfielder.Core;

/// <summary>
/// Hard limits shared by every component (same values as the Kotlin core). Inputs are checked against these
/// before memory is allocated for them.
/// </summary>
public static class Limits
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxHandshakeFrameBytes = 64 * 1024;
    public const int MaxInlineClipBytes = 6 * 1024 * 1024;
    public const int MaxClipTextBytes = 1024 * 1024;
    public const int MaxDeviceNameChars = 64;
    public const int MaxFilesPerOffer = 1000;
    public const int MaxFileNameChars = 150;
    public const int TransferIdHexChars = 32;
    public const int ProtocolVersion = 1;
}

/// <summary>The peer violated the protocol; the connection must be closed.</summary>
public sealed class ProtocolException : IOException
{
    public ProtocolException() { }
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
