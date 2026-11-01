using Xunit;

namespace Dosai.Tests;

public class CorpusLocationTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "home");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToTheSandboxWhenUnsetOrBlank(string? configured)
    {
        Assert.Equal(Path.Combine(Home, "sandbox", "dosai-corpus"), CorpusLocation.Resolve(configured, Home));
    }

    [Fact]
    public void Resolve_UsesTheConfiguredDirectoryAsGiven()
    {
        var configured = Path.Combine(Path.GetTempPath(), "elsewhere", "corpus");
        Assert.Equal(configured, CorpusLocation.Resolve(configured, Home));
    }

    [Fact]
    public void Resolve_ExpandsOnlyALeadingTilde()
    {
        Assert.Equal(Path.Combine(Home, "src", "corpus"), CorpusLocation.Resolve("~/src/corpus", Home));
        Assert.Equal(Home, CorpusLocation.Resolve("~", Home));
        Assert.Equal("/data/~/corpus", CorpusLocation.Resolve("/data/~/corpus", Home));
        Assert.Equal("~other/corpus", CorpusLocation.Resolve("~other/corpus", Home));
    }
}
