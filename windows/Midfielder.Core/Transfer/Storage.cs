namespace Midfielder.Core.Transfer;

/// <summary>Something we can send (a file, a clipboard image...).</summary>
public interface IFileSource
{
    string Name { get; }
    long Size { get; }
    string Mime { get; }
    long ModifiedMs { get; }

    /// <summary>Opens the content from the beginning. The caller disposes it.</summary>
    Stream Open();
}

/// <summary>
/// Where a received file goes. Data lands in a partial location first so an interrupted transfer resumes,
/// and only a verified file is committed.
/// </summary>
public interface IFileSink
{
    long PartialLength();
    Stream OpenPartial();

    /// <summary>Opens for writing at <paramref name="offset"/>, discarding anything after it.</summary>
    Stream OpenAt(long offset);

    /// <summary>Moves the verified file into place and returns its path.</summary>
    string Commit();

    void Discard();
}

public sealed class PathFileSource : IFileSource
{
    private readonly string _path;

    public PathFileSource(string path, string mime = "application/octet-stream")
    {
        _path = path;
        var info = new FileInfo(path);
        Name = info.Name;
        Size = info.Length;
        Mime = mime;
        ModifiedMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
    }

    public string Name { get; }
    public long Size { get; }
    public string Mime { get; }
    public long ModifiedMs { get; }

    public Stream Open() => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
}

public sealed class BytesFileSource(string name, byte[] bytes, string mime) : IFileSource
{
    public string Name { get; } = name;
    public long Size => bytes.LongLength;
    public string Mime { get; } = mime;
    public long ModifiedMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Stream Open() => new MemoryStream(bytes, writable: false);
}

/// <summary>
/// Receives into a directory. Partial data lives in <c>.midfielder-partial/&lt;key&gt;.part</c>; commit moves it to
/// a unique sanitized name and never overwrites an existing file.
/// </summary>
public sealed class DirectoryFileSink : IFileSink
{
    private const string PartialDir = ".midfielder-partial";
    private const int MaxNameAttempts = 10_000;
    private readonly string _directory;
    private readonly string _finalName;
    private readonly string _partial;

    public DirectoryFileSink(string directory, string key, string fileName)
    {
        _directory = directory;
        _finalName = FileNameSanitizer.Sanitize(fileName);
        _partial = Path.Combine(directory, PartialDir, FileNameSanitizer.Sanitize(key) + ".part");
    }

    public long PartialLength() => File.Exists(_partial) ? new FileInfo(_partial).Length : 0;

    public Stream OpenPartial() => new FileStream(_partial, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);

    public Stream OpenAt(long offset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_partial)!);
        var fs = new FileStream(_partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        try
        {
            fs.SetLength(offset);
            fs.Position = offset;
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public string Commit()
    {
        string stem = Path.GetFileNameWithoutExtension(_finalName);
        string ext = Path.GetExtension(_finalName);
        if (_finalName.LastIndexOf('.') <= 0)
        {
            stem = _finalName;
            ext = "";
        }
        for (int i = 0; i < MaxNameAttempts; i++)
        {
            string candidate = Path.Combine(_directory, i == 0 ? _finalName : $"{stem} ({i}){ext}");
            if (File.Exists(candidate)) continue;
            try
            {
                File.Move(_partial, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Lost a race for this name; try the next one.
            }
        }
        throw new IOException($"no free file name for {_finalName}");
    }

    public void Discard()
    {
        if (File.Exists(_partial)) File.Delete(_partial);
    }
}
