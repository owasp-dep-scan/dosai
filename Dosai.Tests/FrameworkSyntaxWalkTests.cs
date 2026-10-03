using Depscan;
using Depscan.Frameworks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Dosai.Tests;

// The framework providers' declaration walks skip statements and expressions, and their
// invocation walks are skipped for trees without a matching invocation name. Both must hand the
// providers exactly the nodes the full walks did.
public partial class DosaiTests
{
    private const string OddDeclarationShapes = """
        global using System;
        using Alias = System.Collections.Generic.List<int>;
        var top = Local(1);
        int Local(int x) { int Inner() => x; return Inner(); }
        Console.WriteLine(new Func<int>(() => { var q = 1; return q; })());
        namespace A.B;
        public record R(int X) { public class Nested { void M() { } struct S { interface I { void N(); } } } }
        public static class E
        {
            extension(string s) { public int Twice() => s.Length * 2; }
            public static void Run() { void Local() { } Local(); _ = new { A = 1 }; }
        }
        public partial class P { partial void Q(); ~P() { } public static P operator +(P a, P b) => a; public P() : this(1) { } P(int x) { } }
        enum Color { Red }
        delegate void D();
        class Broken { void M( { class InBody { } } int F = ; }
        """;

    private static IEnumerable<SyntaxTree> DeclarationWalkTrees()
    {
        yield return CSharpSourceParser.Parse(OddDeclarationShapes, "odd.cs", Directory.GetCurrentDirectory());
        foreach (var file in Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            yield return CSharpSourceParser.Parse(File.ReadAllText(file), file, Directory.GetCurrentDirectory());
        }
    }

    [Fact]
    public void FrameworkDeclarations_YieldWhatTheFullWalkYields()
    {
        var trees = DeclarationWalkTrees().ToList();
        Assert.True(trees.Count > 5);
        foreach (var tree in trees)
        {
            Assert.Equal(tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>(), FrameworkContext.Declarations<TypeDeclarationSyntax>(tree));
            Assert.Equal(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), FrameworkContext.Declarations<MethodDeclarationSyntax>(tree));
            Assert.Equal(tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>(), FrameworkContext.Declarations<UsingDirectiveSyntax>(tree));
        }

        Assert.Contains(FrameworkContext.Declarations<TypeDeclarationSyntax>(trees[0]), type => type.Identifier.Text == "I");
    }

    [Fact]
    public void FrameworkInvocationsNamed_SkipsOnlyTreesWithoutAMatchingName()
    {
        var withMap = CSharpSyntaxTree.ParseText("class C { void M(object app) { app.ToString(); Map(1); app.MapGet<int>(\"/x\", () => 1).WithName(\"n\"); } void Map(int x) { } }", path: "a.cs");
        var withoutMap = CSharpSyntaxTree.ParseText("class D { void M() { System.Console.WriteLine(1); Generic<int>(); } void Generic<T>() { } }", path: "b.cs");
        var compilation = CSharpCompilation.Create("t", [withMap, withoutMap]);
        var context = FrameworkContext.FromCompilations(Directory.GetCurrentDirectory(), compilation, null, PackageUrlResolver.Create(Directory.GetCurrentDirectory()));

        Assert.Equal(withMap.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), context.InvocationsNamed(withMap, name => name == "MapGet"));
        Assert.Empty(context.InvocationsNamed(withoutMap, name => name == "MapGet"));
        // InvocationName's own rules: a bare generic call has no name, a generic member call has its identifier.
        Assert.True(context.Invokes(withoutMap, name => name.Length == 0));
        Assert.False(context.Invokes(withoutMap, name => name == "Generic"));
        Assert.True(context.Invokes(withMap, name => name == "WithName"));
        // A tree outside the compilation has no index and is walked.
        var outside = CSharpSyntaxTree.ParseText("class E { void M() { N(); } void N() { } }");
        Assert.Single(context.InvocationsNamed(outside, name => name == "Nothing"));
    }
}
