using Depscan;
using Xunit;

namespace Dosai.Tests;

public class PathExclusionsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dosai-exclusions-root");

    private static bool Excluded(string relativePath, bool isDirectory, params string[] patterns)
    {
        using var scope = PathExclusions.Apply(Root, patterns);
        return PathExclusions.IsExcluded(Path.Combine(Root, relativePath), isDirectory);
    }

    [Theory]
    // Anchored directory globs, as cdxgen passes them.
    [InlineData("BuildOutput/**", "BuildOutput", true, true)]
    [InlineData("BuildOutput/**", "BuildOutput/Vendor.dll", false, true)]
    [InlineData("BuildOutput/**", "BuildOutput/x64/deep/Vendor.dll", false, true)]
    [InlineData("BuildOutput/**", "src/BuildOutput/Vendor.dll", false, false)]
    [InlineData("BuildOutput/**", "BuildOutputs/Vendor.dll", false, false)]
    [InlineData("/BuildOutput", "BuildOutput/Vendor.dll", false, true)]
    [InlineData("./src/Legacy", "src/Legacy/Old.cs", false, true)]
    [InlineData("src/Legacy", "Legacy/Old.cs", false, false)]
    // Unanchored names match at any depth.
    [InlineData("node_modules", "web/app/node_modules/pkg/index.js", false, true)]
    [InlineData("*.Designer.cs", "src/Forms/Main.Designer.cs", false, true)]
    [InlineData("*.Designer.cs", "src/Forms/Main.cs", false, false)]
    [InlineData("Main.?s", "src/Main.cs", false, true)]
    // Globstar in the middle and at the start.
    [InlineData("**/test/**", "src/test/UnitTests.cs", false, true)]
    [InlineData("**/test/**", "test/UnitTests.cs", false, true)]
    [InlineData("src/**/Generated/*.cs", "src/a/b/Generated/Api.cs", false, true)]
    [InlineData("src/**/Generated/*.cs", "src/Generated/Api.cs", false, true)]
    [InlineData("src/**/Generated/*.cs", "src/Generated/nested/Api.cs", false, false)]
    // A single star stays within one segment.
    [InlineData("src/*.cs", "src/a/Program.cs", false, false)]
    [InlineData("src/*.cs", "src/Program.cs", false, true)]
    // A trailing slash restricts the pattern to directories.
    [InlineData("bin/", "bin", false, false)]
    [InlineData("bin/", "bin/App.dll", false, true)]
    // Windows separators in a pattern are accepted.
    [InlineData(@"BuildOutput\**", "BuildOutput/Vendor.dll", false, true)]
    public void IsExcluded_MatchesGlobSemantics(string pattern, string relativePath, bool isDirectory, bool expected)
    {
        Assert.Equal(expected, Excluded(relativePath, isDirectory, pattern));
    }

    [Fact]
    public void IsExcluded_NeverExcludesTheRootOrPathsOutsideIt()
    {
        using var scope = PathExclusions.Apply(Root, ["**"]);

        Assert.False(PathExclusions.IsExcluded(Root, isDirectory: true));
        Assert.False(PathExclusions.IsExcluded(Path.Combine(Path.GetTempPath(), "shared", "System.Runtime.dll"), isDirectory: false));
        Assert.True(PathExclusions.IsExcluded(Path.Combine(Root, "anything.cs"), isDirectory: false));
    }

    [Fact]
    public void Apply_ScopeEndsOnDisposeAndRestoresTheOuterScope()
    {
        var path = Path.Combine(Root, "BuildOutput", "Vendor.dll");
        using (PathExclusions.Apply(Root, ["BuildOutput"]))
        {
            using (PathExclusions.Apply(Root, ["*.cs"]))
            {
                Assert.False(PathExclusions.IsExcluded(path, isDirectory: false));
            }
            Assert.True(PathExclusions.IsExcluded(path, isDirectory: false));
        }
        Assert.False(PathExclusions.IsExcluded(path, isDirectory: false));
    }

    [Theory]
    [InlineData("!BuildOutput")]
    [InlineData("  ")]
    [InlineData("/")]
    public void Apply_RejectsNegatedAndEmptyPatterns(string pattern)
    {
        Assert.Throws<ArgumentException>(() => PathExclusions.Apply(Root, [pattern]));
    }

    [Fact]
    public void GetMethods_SkipsExcludedFilesAndDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Write(root, "src/Keep.cs", "namespace App; public class Keep { public void Run() { } }");
            Write(root, "src/Main.Designer.cs", "namespace App; public class Designer { public void Layout() { } }");
            Write(root, "BuildOutput/Generated/Drop.cs", "namespace App; public class Drop { public void Gone() { } }");

            MethodsSlice slice;
            using (PathExclusions.Apply(root, ["BuildOutput/**", "*.Designer.cs"]))
            {
                slice = Depscan.Dosai.GetMethodsSlice(root);
            }

            var classes = slice.Methods!.Select(method => method.ClassName).ToHashSet();
            Assert.Contains("Keep", classes);
            Assert.DoesNotContain("Designer", classes);
            Assert.DoesNotContain("Drop", classes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
