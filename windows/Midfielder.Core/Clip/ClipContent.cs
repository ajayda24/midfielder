using System.Security.Cryptography;
using System.Text;
using Midfielder.Core.Crypto;

namespace Midfielder.Core.Clip;

/// <summary>Clipboard content in a platform-neutral form: UTF-8 text or PNG bytes.</summary>
public abstract class ClipContent
{
    private byte[]? _sha256;

    public abstract bool Sensitive { get; }
    public abstract string Mime { get; }
    public abstract ReadOnlyMemory<byte> Bytes { get; }

    public byte[] Sha256 => _sha256 ??= SHA256.HashData(Bytes.Span);
    public string Sha256Hex => Hex.Encode(Sha256);
}

public sealed class TextClip(string text, bool sensitive = false) : ClipContent
{
    private byte[]? _bytes;
    public string Text { get; } = text;
    public override bool Sensitive { get; } = sensitive;
    public override string Mime => "text/plain";
    public override ReadOnlyMemory<byte> Bytes => _bytes ??= Encoding.UTF8.GetBytes(Text);
}

public sealed class ImageClip(byte[] png, bool sensitive = false) : ClipContent
{
    public override bool Sensitive { get; } = sensitive;
    public override string Mime => "image/png";
    public override ReadOnlyMemory<byte> Bytes { get; } = png;
}
