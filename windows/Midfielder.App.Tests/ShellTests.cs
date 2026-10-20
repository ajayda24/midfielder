using Microsoft.Win32;
using Xunit;

namespace Midfielder.App.Tests;

/// <summary>Explorer right-click entry, written to the runner's HKCU.</summary>
[Collection("registry")]
public sealed class ShellTests
{
    [Fact]
    public void ContextMenuRegistersAndUnregisters()
    {
        const string exe = @"C:\Program Files\Midfielder\Midfielder.exe";
        try
        {
            Shell.SetContextMenu(true, exe);
            using (var key = Registry.CurrentUser.OpenSubKey(Shell.ContextMenuKey))
            {
                Assert.NotNull(key);
                Assert.Equal("Send to phone", key.GetValue(""));
                Assert.Equal("Player", key.GetValue("MultiSelectModel"));
                using var command = key.OpenSubKey("command");
                Assert.Equal($"\"{exe}\" --send \"%1\"", command!.GetValue(""));
            }
            Shell.SetContextMenu(false);
            Assert.Null(Registry.CurrentUser.OpenSubKey(Shell.ContextMenuKey));
            Shell.SetContextMenu(false); // removing twice is fine
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(Shell.ContextMenuKey, throwOnMissingSubKey: false);
        }
    }
}
