using Depscan;
using Xunit;

namespace Depscan.Tests;

public class SourceDocumentPathsTests
{
    [Fact]
    public void RelativePath_RewritesBackslashesOnWindowsOnly()
    {
        var root = System.IO.Path.GetTempPath();
        // On Unix a backslash is an ordinary file name character and must survive.
        var path = System.IO.Path.Combine(root, "dir", "a\\b.cs");
        var relative = SourceDocumentPaths.RelativePath(root, path);
        Assert.Equal(OperatingSystem.IsWindows() ? "dir/a/b.cs" : "dir/a\\b.cs", relative);
    }
}
