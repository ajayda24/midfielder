using System.Xml.Linq;
using Xunit;

namespace Midfielder.App.Tests;

/// <summary>
/// Settings in Midfielder.App.csproj that only the published exe sees (the tests run with their own runtime
/// config, so they can't catch these by running the window).
/// </summary>
public sealed class BuildSettingsTests
{
    [Fact]
    public void AppKeepsCultureDataForWpf()
    {
        // InvariantGlobalization made every WPF window fail in the real exe: "Cannot find non-neutral culture
        // related to 'en-us'" while parsing templates, and a FontCache type initializer error in text layout.
        var project = XDocument.Load(AppProject());
        var invariant = project.Descendants("InvariantGlobalization").Select(e => e.Value.Trim()).LastOrDefault();
        Assert.NotEqual("true", invariant, StringComparer.OrdinalIgnoreCase);
    }

    private static string AppProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Midfielder.App", "Midfielder.App.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Midfielder.App.csproj not found above " + AppContext.BaseDirectory);
    }
}
