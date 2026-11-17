using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     The MSBuild comment stripper and the import and item scans. Comment stripping and the
///     import scan were lazy regular expressions, so a file of unclosed comment openers or
///     unclosed import tags made every opener walk the remaining text once: quadratic time on a
///     truncated or hostile project file. The linear scanners must produce exactly the old
///     expressions' output on well-formed input; the old expressions are kept here as the
///     oracle. An unclosed comment is the one deliberate difference: it runs to the end of the
///     text, the reading of an opener with no closer that MSBuild itself would reject.
/// </summary>
public class MsBuildScannerTests
{
    /// <summary>The hostile parse target: two megabytes with no closing comment or import tag in it.</summary>
    private const int HostileBytes = 2 * 1024 * 1024;

    // The expressions the scanners replaced. They are the oracle for well-formed input and stay
    // out of the production code.
    private static readonly Regex CommentOracle = new(@"<!--.*?-->", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex ImportOracle = new(@"<Import\s+([^>]*?)(?:/>|>.*?</Import\s*>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static Regex ItemOracle(string name) => new(
        $@"<{name}\s+([^>]*?)(?:/>|>(.*?)</{name}\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [Fact]
    public void AFileOfUnclosedCommentsAndImportsParsesQuickly()
    {
        // The unclosed imports come first so the import scan still sees them after the stripper
        // removes the unclosed comment that swallows the second half of the file.
        var hostile = new StringBuilder(HostileBytes);
        while (hostile.Length < HostileBytes / 2)
        {
            hostile.Append("<Import Project=\"missing.props\" ");
        }

        while (hostile.Length < HostileBytes)
        {
            hostile.Append("<!--");
        }

        using var directory = new TempDir();
        var project = Write(directory.Path, "hostile.csproj", hostile.ToString());

        var clock = Stopwatch.StartNew();
        var segments = MsBuildImports.Expand(directory.Path, project);
        clock.Stop();

        Assert.Single(segments);
        Assert.StartsWith("<Import", segments[0].Content, StringComparison.Ordinal);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"expanding {hostile.Length} bytes took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void StrippedCommentsMatchTheCommentRegex()
    {
        foreach (var content in CommentCorpus)
        {
            Assert.Equal(CommentOracle.Replace(content, string.Empty), MsBuildImports.WithoutXmlComments(content));
        }
    }

    [Fact]
    public void ItemsMatchTheItemRegex()
    {
        foreach (var content in ItemCorpus)
        {
            AssertItemsEqual(ItemOracle("Using").Matches(content).Cast<Match>().ToList(), MsBuildImports.Items(Segment("x.csproj", content), "Using").ToList());
        }

        // The expansion splits a file into segments at every import, and the item scan runs per
        // segment: a file cut mid-element must agree with the oracle run over each half.
        var cut = ItemCorpus[0];
        var halves = new[] { cut[..(cut.Length / 2)], cut[(cut.Length / 2)..] };
        var expected = halves.SelectMany(half => ItemOracle("Using").Matches(half).Cast<Match>()).ToList();
        AssertItemsEqual(expected, MsBuildImports.Items(Segment("a.props", halves[0]).Concat(Segment("b.targets", halves[1])).ToList(), "Using").ToList());
    }

    [Fact]
    public void ImportsSplitSegmentsLikeTheImportRegex()
    {
        foreach (var content in ProjectCorpus)
        {
            using var directory = new TempDir();
            var project = Write(directory.Path, "project.csproj", content);
            var segments = MsBuildImports.Expand(directory.Path, project);

            // None of the corpus imports names a file that exists, so every matched span is
            // dropped in place and the own-file text is the stripped content cut at the spans.
            Assert.Equal(ExpectedOwnSegments(content), segments.Select(segment => segment.Content).ToArray());
            Assert.All(segments, segment => Assert.Equal(Path.GetFullPath(project), segment.File));
            Assert.All(segments, segment => Assert.False(segment.Conditioned));
        }
    }

    [Fact]
    public void AnUnclosedCommentRunsToTheEndOfTheText()
    {
        Assert.Equal("<Project />\n", MsBuildImports.WithoutXmlComments("<Project />\n<!-- never closed"));
        Assert.Equal("a b ", MsBuildImports.WithoutXmlComments("a<!-- x --> b <!-- y"));
    }

    [Fact]
    public void RandomTextsMatchTheOldExpressions()
    {
        var random = new Random(20261117);
        string[] fragments = ["<Import", "<Using", " ", "\t", "\r\n", "Project=\"a\"", "Include=\"b\"", "Condition=\"x\"", "/", ">", "<!--", "-->", "</Import>", "</Import >", "</Using>", "text", "<", "!", "-", "="];
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var parts = new List<string>();
            for (var i = random.Next(3, 40); i > 0; i--)
            {
                parts.Add(fragments[random.Next(fragments.Length)]);
            }

            var text = string.Concat(parts);
            var wellFormed = CloseOpenComments(text);

            // The strip agrees with the oracle once every opener has its closer.
            Assert.Equal(CommentOracle.Replace(wellFormed, string.Empty), MsBuildImports.WithoutXmlComments(wellFormed));

            // The item scan agrees on any text, well-formed or not.
            AssertItemsEqual(ItemOracle("Using").Matches(text).Cast<Match>().ToList(), MsBuildImports.Items(Segment("x.csproj", text), "Using").ToList());

            // The import scan agrees the same way: the own-file pieces of an expansion are the
            // stripped text cut at the oracle's spans (none of the imports names an existing file).
            using var directory = new TempDir();
            var project = Write(directory.Path, "project-" + iteration + ".csproj", wellFormed);
            Assert.Equal(ExpectedOwnSegments(wellFormed), MsBuildImports.Expand(directory.Path, project).Select(segment => segment.Content).ToArray());
        }
    }

    /// <summary>The text with a closer appended for every comment opener that has none, so a strip comparison runs on well-formed input.</summary>
    private static string CloseOpenComments(string text)
    {
        var builder = new StringBuilder(text);
        while (CommentOracle.Replace(builder.ToString(), string.Empty).Contains("<!--", StringComparison.Ordinal))
        {
            builder.Append("-->");
        }

        return builder.ToString();
    }

    private static void AssertItemsEqual(List<Match> expected, List<MsBuildImports.MsBuildItem> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Index, actual[i].Match.Index);
            Assert.Equal(expected[i].Length, actual[i].Match.Length);
            Assert.Equal(expected[i].Groups[1].Value, actual[i].Match.Groups[1].Value);
            Assert.Equal(expected[i].Groups[2].Success, actual[i].Match.Groups[2].Success);
            Assert.Equal(expected[i].Groups[2].Value, actual[i].Match.Groups[2].Value);
        }
    }

    private static List<MsBuildImports.MsBuildFileSegment> Segment(string file, string content) => [new(file, content, Conditioned: false)];

    /// <summary>The own-file pieces of an expansion: the stripped text with every oracle import span excised, empty pieces dropped.</summary>
    private static string[] ExpectedOwnSegments(string content)
    {
        var stripped = CommentOracle.Replace(content, string.Empty);
        var kept = new List<string>();
        var position = 0;
        foreach (Match import in ImportOracle.Matches(stripped))
        {
            if (import.Index > position)
            {
                kept.Add(stripped[position..import.Index]);
            }

            position = import.Index + import.Length;
        }

        if (position < stripped.Length)
        {
            kept.Add(stripped[position..]);
        }

        return kept.Where(piece => piece.Length > 0).ToArray();
    }

    private static readonly string[] ProjectCorpus =
    [
        """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""",
        """
<Project>
  <Import Project="missing-a.props" />
</Project>
""",
        """
<Project>
  <Import Project="missing-a.props"/>
  <Import Project="missing-b.props"   />
</Project>
""",
        """
<Project>
  <Import Project="missing-a.props" Condition="'$(Configuration)' == 'Debug'" />
  <Import Project="missing-b.props" Condition="Exists('missing-exists.props')" />
  <Import Project="missing-c.props" Condition="Exists('$(Unresolvable)')" />
</Project>
""",
        """
<Project>
  <Import Project="missing-a.props" Sdk="Microsoft.NET.Sdk" />
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
""",
        """
<project>
  <import project="missing-a.props" />
</project>
""",
        """
<Project>
  <Import Project="missing-a.props">body text</Import>
  <Import Project="missing-b.props"></Import >
</Project>
""",
        "<Project>\r\n  <Import Project=\"missing-a.props\">\r\n    body\r\n  </Import\n  >\r\n</Project>\r\n",
        """
<Project>
  <Import Project="missing-a.props" /><Import Project="missing-b.props" />
</Project>
""",
        """
<Project>
  <Import Project="missing-a.props"><Import Project="missing-b.props" /></Import>
</Project>
""",
        """
<Project>
  <Imports Include="not-an-import.props" />
  <Import />
</Project>
""",
        """
<Project>
  <Import Project="missing-a.props"
  <Import Project="missing-b.props">never closed
</Project>
""",
        """
<Project>
  <Import/>
  <Import Project="dir/missing-a.props" />
</Project>
""",
        // The quote-blind quirk: the scan does not know quoted attribute values, so a '>' inside
        // one ends the attribute run. Without a closing tag the element does not match.
        """
<Project>
  <Import Project="a>b.props" />
  <Import Project="a>b.props">kept</Import>
</Project>
""",
        """
<Project>
  <Import	Project="missing-a.props"	/>
</Project>
""",
        """
<!-- a leading comment -->
<Project>
  <!-- one --> text <!-- two --> tail
  <Import Project="missing-a.props" /> <!-- <Import Project="commented-out.props" /> -->
  <!--
    a comment
    spanning lines -- with dashes
  -->
  <Import Project="missing-b.props" />
</Project>
<!-- a trailing comment -->
""",
        "text before <!-- first --> between <!--- second ---> after <!----> end",
        """
<Project>
  <PropertyGroup>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""".Replace("\n", "\r\n"),
        "no comments or imports at all",
        "--> stray closer with no opener",
    ];

    private static readonly string[] ItemCorpus =
    [
        """
<ItemGroup>
  <Using Include="System" />
  <Using Include="System.Linq"/>
  <Using   Include="System.Threading"   />
</ItemGroup>
""",
        """
<ItemGroup>
  <Using Include="A" Condition="'$(A)' == 'B'" />
  <Using Remove="B" />
</ItemGroup>
""",
        """
<ItemGroup>
  <Using Include="A">keep this</Using>
  <Using Include="B"></Using>
</ItemGroup>
""",
        "<ItemGroup>\r\n  <Using Include=\"A\">\r\n    keep\r\n  </Using>\r\n</ItemGroup>",
        """
<itemgroup>
  <using include="a" />
</itemgroup>
""",
        """
<ItemGroup>
  <Using />
  <Using/>
</ItemGroup>
""",
        """
<ItemGroup>
  <Using Include="a<b" />
  <Using Include="a>b">kept</Using>
  <Using Include='single quoted' />
</ItemGroup>
""",
        """
<ItemGroup>
  <Using Include="A"><Using Include="B" /></Using>
</ItemGroup>
""",
        """
<ItemGroup>
  <Using Include="$(Framework)" /><Using Include="adjacent.props" />
  <Using
    Include="wrapped"
  />
</ItemGroup>
""",
        """
<ItemGroup>
  <Using Include="never closed"
  <Using Include="also never">closed nowhere
</ItemGroup>
""",
        """
<Project>
  <Compile Include="a/b.cs" />
</Project>
""",
    ];

    private static readonly string[] CommentCorpus =
    [
        .. ProjectCorpus,
        "a<!-- b -->c",
        "<!-- one --> text <!-- two --> tail",
        "<!-- -- -->",
        "<!-- a--b -->",
        "<!---->",
        "<!--- --->",
        "<!--\n multi\n line\n-->",
        "text <!-- c -->\r\nmore <!-- d -->\r\nend",
    ];

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-msbuild-scanners-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
