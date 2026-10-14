using System.Text;

namespace Midfielder.Core.Transfer;

/// <summary>
/// Turns a name from a peer into a safe file name (same rules as the Kotlin core): no directories, no path
/// traversal, no reserved device names, no control characters, bounded length.
/// </summary>
public static class FileNameSanitizer
{
    private const string Fallback = "file";
    private const int MaxExtensionChars = 16;
    private static readonly HashSet<char> Illegal = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> Reserved = BuildReserved();

    public static string Sanitize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string name = raw[(Math.Max(raw.LastIndexOf('/'), raw.LastIndexOf('\\')) + 1)..];
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(c < 0x20 || c == 0x7F || Illegal.Contains(c) ? '_' : c);
        string cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        string result = cleaned.Length == 0 || cleaned.All(c => c == '.') ? Fallback : cleaned;
        int dot = result.IndexOf('.', StringComparison.Ordinal);
        string stem = dot >= 0 ? result[..dot] : result;
        if (Reserved.Contains(stem.ToUpperInvariant())) result = "_" + result;
        return Truncate(result);
    }

    private static string Truncate(string name)
    {
        if (name.Length <= Limits.MaxFileNameChars) return name;
        int dot = name.LastIndexOf('.');
        string ext = dot > 0 && name.Length - dot <= MaxExtensionChars ? name[dot..] : "";
        string stem = name[..(name.Length - ext.Length)];
        if (stem.Length > Limits.MaxFileNameChars - ext.Length) stem = stem[..(Limits.MaxFileNameChars - ext.Length)];
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        return stem + ext;
    }

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };
        for (int i = 0; i <= 9; i++)
        {
            set.Add($"COM{i}");
            set.Add($"LPT{i}");
        }
        return set;
    }
}
