package dev.midfielder.core

/**
 * Hard limits shared by every component. Inputs are checked against these *before* any memory is allocated
 * for them, so a misbehaving or malicious peer can't make us allocate unbounded memory.
 */
public object Limits {
    /** Largest frame accepted from an authenticated (paired) peer. */
    public const val MAX_FRAME_BYTES: Int = 8 * 1024 * 1024

    /** Largest frame accepted before the peer is authenticated (Hello / pairing). */
    public const val MAX_HANDSHAKE_FRAME_BYTES: Int = 64 * 1024

    /** Clipboard content larger than this is sent as a transfer instead of inline. */
    public const val MAX_INLINE_CLIP_BYTES: Int = 6 * 1024 * 1024

    /** Largest clipboard text we will sync at all (1 MiB is roughly a 500-page book). */
    public const val MAX_CLIP_TEXT_BYTES: Int = 1024 * 1024

    public const val MAX_DEVICE_NAME_CHARS: Int = 64
    public const val MAX_FILES_PER_OFFER: Int = 1000
    public const val MAX_FILE_NAME_CHARS: Int = 150
    public const val TRANSFER_ID_HEX_CHARS: Int = 32
    public const val PROTOCOL_VERSION: Int = 1
}

/** The peer violated the protocol. The connection must be closed. */
public class ProtocolException(message: String, cause: Throwable? = null) : java.io.IOException(message, cause)
