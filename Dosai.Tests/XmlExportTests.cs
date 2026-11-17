using System.Security;
using System.Xml;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     XML 1.0 rejects control characters below U+0020 (tab, newline and carriage return
///     excepted), U+FFFE and U+FFFF, and any surrogate that is not half of a complete pair.
///     Identifiers, file names and code text from an analyzed tree can carry all of those, so
///     the GraphML and GEXF writers must replace them instead of writing a document no XML
///     parser accepts (docs/graph-formats.md describes the replacement).
/// </summary>
public class XmlExportTests
{
    [Fact]
    public void DataFlowXmlExports_ParseWhenTextCarriesXmlInvalidCharacters()
    {
        var result = new DataFlowResult
        {
            Nodes =
            [
                new DataFlowNode
                {
                    Id = "n\u0001",
                    Kind = "parameter",
                    Name = "ctl\u0001\u000B\r\n\t",
                    Symbol = "System.\u0001Api",
                    Code = "x = \"a\u000Bb\" & <tag>",
                    Category = "cli",
                    FileName = "F\u0001le.cs",
                    MethodName = "M\uDC00",
                    LineNumber = 3
                },
                new DataFlowNode
                {
                    Id = "n\uD800",
                    Kind = "local",
                    Name = "loneHigh\uD800",
                    FileName = "G\uD800.cs",
                    LineNumber = 4
                }
            ],
            Edges =
            [
                new DataFlowEdge { Id = "e\u000B1", SourceId = "n\u0001", TargetId = "n\uD800", Kind = "assign\u000C", Label = "l\uFFFE", LineNumber = 5 }
            ]
        };

        foreach (var format in new[] { DataFlowExportFormat.GraphMl, DataFlowExportFormat.Gexf })
        {
            var exported = DataFlowExporter.Export(result, format);
            AssertParsesAndCarriesReplacement(exported);
        }
    }

    [Fact]
    public void CallGraphXmlExports_ParseWhenTextCarriesXmlInvalidCharacters()
    {
        var graph = new CallGraph
        {
            Nodes =
            [
                new MethodNode { Id = "A.M\u0001():void", Name = "M", Label = "l\u0001bel", ClassName = "A", Namespace = "N", FileName = "A\u000B.cs", Kind = "method" },
                new MethodNode { Id = "B.N\uD800():void", Name = "N", Label = "loneHigh\uD800", ClassName = "B", Namespace = "", FileName = "B.cs", Kind = "method", Purl = "pkg:nuget/B@1.0.0" }
            ],
            Edges =
            [
                new MethodCallEdge
                {
                    SourceId = "A.M\u0001():void",
                    TargetId = "B.N\uD800():void",
                    CallType = CallType.MethodCall,
                    CallSiteCount = 1,
                    CallLocation = new CallLocation { FileName = "A\uFFFE.cs", LineNumber = 2, ColumnNumber = 3 }
                }
            ]
        };

        foreach (var format in new[] { CallGraphExportFormat.GraphMl, CallGraphExportFormat.Gexf })
        {
            var exported = CallGraphExporter.Export(graph, format);
            AssertParsesAndCarriesReplacement(exported);
        }
    }

    [Fact]
    public void XmlExports_KeepLegalLabelTextUnchanged()
    {
        // Every escape-relevant but XML-legal character: the five entities, CJK, a supplementary
        // pair, tab and newline. The bytes must stay what SecurityElement.Escape produced before
        // the escaper learned about invalid characters.
        var nasty = "argv\" 'quoted' <tag> & amp é日本語\ttab\nnl \U0001F600 \"\" end";
        var expected = "argv&quot; &apos;quoted&apos; &lt;tag&gt; &amp; amp é日本語\ttab\nnl \U0001F600 &quot;&quot; end";
        Assert.Equal(expected, SecurityElement.Escape(nasty));

        var dataFlowResult = new DataFlowResult
        {
            Nodes = [new DataFlowNode { Id = "n1", Kind = "parameter", Name = nasty, Symbol = nasty, Code = nasty, LineNumber = 3 }],
            Edges = [new DataFlowEdge { Id = "e1", SourceId = "n1", TargetId = "n1", Kind = nasty, Label = nasty, LineNumber = 4 }]
        };
        foreach (var format in new[] { DataFlowExportFormat.GraphMl, DataFlowExportFormat.Gexf })
        {
            Assert.Contains(expected, DataFlowExporter.Export(dataFlowResult, format), StringComparison.Ordinal);
        }

        var callGraph = new CallGraph
        {
            Nodes = [new MethodNode { Id = "A.M():void", Name = "M", Label = nasty, ClassName = "A", Namespace = "N", FileName = nasty + ".cs", Kind = "method" }],
            Edges =
            [
                new MethodCallEdge
                {
                    SourceId = "A.M():void",
                    TargetId = "A.M():void",
                    CallType = CallType.MethodCall,
                    CallLocation = new CallLocation { FileName = nasty + ".cs", LineNumber = 2, ColumnNumber = 3 }
                }
            ]
        };
        foreach (var format in new[] { CallGraphExportFormat.GraphMl, CallGraphExportFormat.Gexf })
        {
            Assert.Contains(expected, CallGraphExporter.Export(callGraph, format), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void XmlText_EscapesTheFiveMarkupEntities()
    {
        Assert.Equal(string.Empty, XmlText.Escape(null));
        Assert.Equal(string.Empty, XmlText.Escape(string.Empty));
        Assert.Equal("&lt;tag&gt;", XmlText.Escape("<tag>"));
        Assert.Equal("a &amp; b", XmlText.Escape("a & b"));
        Assert.Equal("&apos;quoted&apos;", XmlText.Escape("'quoted'"));
        Assert.Equal("&quot;", XmlText.Escape("\""));
        Assert.Equal("a&lt;b&gt;c&amp;d&apos;e&quot;f", XmlText.Escape("a<b>c&d'e\"f"));
    }

    [Fact]
    public void XmlText_ReplacesXmlInvalidCharactersWithTheReplacementCharacter()
    {
        Assert.Equal("\uFFFD", XmlText.Escape("\u0001"));
        Assert.Equal("a\uFFFDb", XmlText.Escape("a\u0001b"));
        Assert.Equal("\uFFFD", XmlText.Escape("\u000B"));
        Assert.Equal("\uFFFD", XmlText.Escape("\u000C"));
        Assert.Equal("\uFFFD", XmlText.Escape("\u0000"));
        Assert.Equal("\uFFFD", XmlText.Escape("\u001F"));
        Assert.Equal("\uFFFD", XmlText.Escape("\uFFFE"));
        Assert.Equal("\uFFFD", XmlText.Escape("\uFFFF"));
        Assert.Equal("a\uFFFD&lt;\uFFFD&gt;b", XmlText.Escape("a\u0001<\u000B>b"));
        // XML 1.0 keeps tab, newline and carriage return, and U+007F is inside the Char
        // production, so all four stay.
        Assert.Equal("\t\n\r", XmlText.Escape("\t\n\r"));
        Assert.Equal("\u007F", XmlText.Escape("\u007F"));
    }

    [Fact]
    public void XmlText_ReplacesLoneSurrogatesButKeepsCompletePairs()
    {
        Assert.Equal("\uFFFD", XmlText.Escape("\uD800"));
        Assert.Equal("\uFFFD", XmlText.Escape("\uDBFF"));
        Assert.Equal("\uFFFD", XmlText.Escape("\uDC00"));
        Assert.Equal("\uFFFD", XmlText.Escape("\uDFFF"));
        Assert.Equal("a\uFFFDb", XmlText.Escape("a\uD800b"));
        Assert.Equal("\uFFFD\uFFFD", XmlText.Escape("\uD800\uD800"));
        // The first high surrogate's partner is another high surrogate, so it is lone; the
        // second one pairs with the low surrogate after it and survives.
        Assert.Equal("\uFFFD\uD83D\uDE00", XmlText.Escape("\uD800\uD83D\uDE00"));
        Assert.Equal("\uD83D\uDE00", XmlText.Escape("\uD83D\uDE00"));
        Assert.Equal("\uD83D\uDE00&lt;", XmlText.Escape("\uD83D\uDE00<"));
    }

    [Fact]
    public void XmlText_ReturnsStringsThatNeedNoWorkUnchanged()
    {
        foreach (var plain in new[]
                 {
                     "simple", "Ünïcödé.日本語.A.M():void", "tab\tnewline\nreturn\r", "emoji \U0001F600",
                     "pkg:nuget/B@1.0.0", "delete \u007F"
                 })
        {
            Assert.Same(plain, XmlText.Escape(plain));
        }
    }

    private static void AssertParsesAndCarriesReplacement(string exported)
    {
        using var reader = XmlReader.Create(new StringReader(exported));
        while (reader.Read())
        {
        }

        // Ordinal on purpose: xunit's two-argument string asserts compare with the current
        // culture, and ICU collation treats U+0001 as ignorable, which would report a match on
        // clean text (and the suite must not depend on the host locale).
        Assert.Contains("\uFFFD", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("\u000B", exported, StringComparison.Ordinal);
    }
}
