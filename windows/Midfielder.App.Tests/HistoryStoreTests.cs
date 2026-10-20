using Xunit;
using System.Drawing;
using System.Windows.Forms;

namespace Midfielder.App.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("midfielder-history-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TransferRecord Rec(string id, string name = "photo.jpg") =>
        new(id, TransferDirection.Received, name, "image/jpeg", 10, "Pixel", 1_700_000_000_000, TransferStatus.Done, @"C:\x\" + name);

    [Fact]
    public void KeepsNewestFirstAndBounded()
    {
        using var h = new HistoryStore(null);
        h.Add(Enumerable.Range(0, HistoryStore.MaxRecords + 20).Select(i => Rec(i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var all = h.Records();
        Assert.Equal(HistoryStore.MaxRecords, all.Count);
        Assert.Equal("0", all[0].Id);
        h.Add([Rec("42")]);
        Assert.Equal("42", h.Records()[0].Id);
        Assert.Equal(HistoryStore.MaxRecords, h.Records().Count);
        Assert.Equal(2, h.Remove(["42", "0", "nope"]));
    }

    [Fact]
    public void SurvivesRestartWithStats()
    {
        string path = Path.Combine(_dir, "history.json");
        var today = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);
        using (var h = new HistoryStore(path))
        {
            h.Add([Rec("a", "ünïcode 📷.png"), Rec("b")]);
            h.RecordClip(TransferDirection.Sent, today);
            h.RecordClip(TransferDirection.Received, today.AddDays(-1));
            h.RecordFiles(TransferDirection.Received, 2, 500, today);
        }
        using var back = new HistoryStore(path);
        Assert.Equal(["a", "b"], back.Records().Select(r => r.Id));
        Assert.Equal("ünïcode 📷.png", back.Records()[0].Name);
        Assert.Equal(new Counts(ClipsSent: 1, ClipsReceived: 1, FilesReceived: 2, BytesReceived: 500), back.Totals());
        var days = back.LastDays(7, today.Date);
        Assert.Equal(7, days.Count);
        Assert.Equal(3, days[^1].Clips + days[^1].Files);
        Assert.Equal(1, days[^2].ClipsReceived);
    }

    [Fact]
    public void CorruptFileStartsFresh()
    {
        string path = Path.Combine(_dir, "history.json");
        File.WriteAllText(path, "{ not json");
        using var h = new HistoryStore(path);
        Assert.Empty(h.Records());
    }

    [Fact]
    public void TrackerThrottlesButPublishesCompletion()
    {
        var t = new TransferTracker(minIntervalMs: 10_000);
        int changes = 0;
        t.Changed += () => changes++;
        t.Start("x", TransferDirection.Sent, "a.zip", "Pixel", 100);
        Assert.False(t.Progress("x", 10, 100));
        Assert.True(t.Progress("x", 100, 100));
        Assert.Equal(100, t.Get("x")!.Percent);
        t.Finish("x");
        Assert.Empty(t.Active());
        Assert.Equal(3, changes);
    }
}
