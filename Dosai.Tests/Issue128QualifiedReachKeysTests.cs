using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #128: the reachable-mode dependency walk keys members by the declaring type's
///     full nesting chain, so a namespace-less nested type of one package (a compiler-generated
///     <c>&lt;&gt;c</c> or a user-written <c>Cache</c>) never joins another package's same-named
///     type. A definition whose legacy unqualified key is unique over the whole set keeps a
///     secondary entry, so a lookup that cannot build a qualified chain degrades to today's
///     reach instead of pruning silently.
/// </summary>
public class Issue128QualifiedReachKeysTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue128-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private const string LibPSource = """
using System;

namespace LibP;

public static class P
{
    public static int Run(int value) => Cache.Local(value);

    private static class Cache
    {
        public static int Local(int seed) => seed + 1;
    }
}
""";

    private const string LibQSource = """
using System.Diagnostics;

namespace LibQ;

public static class Q
{
    private static class Cache
    {
        public static void Local(string seed)
        {
            Process.Start("cmd.exe", seed);
        }
    }
}
""";

    private const string AppSource = """
namespace App;

public static class Program
{
    public static void Main(string[] args)
    {
        _ = LibP.P.Run(1);
    }
}
""";

    private static string Emit(string outputDirectory, string assemblyName, string source, params string[] references)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (Microsoft.CodeAnalysis.MetadataReference)entry.Reference).ToList();
        foreach (var reference in references)
        {
            metadata.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(reference));
        }

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(assemblyName,
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: $"{assemblyName}.cs")],
            metadata,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable));
        var dllPath = System.IO.Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var stream = File.Create(dllPath))
        {
            var emit = compilation.Emit(stream);
            Assert.True(emit.Success, assemblyName);
        }

        return dllPath;
    }

    private static string DepsJsonMarkingPackages(params string[] packages)
    {
        var targets = string.Join(",", packages.Select(package => $"\"{package}/1.0.0\": {{ \"runtime\": {{ \"{package}.dll\": {{}} }} }}"));
        var libraries = string.Join(",", packages.Select(package => $"\"{package}/1.0.0\": {{ \"type\": \"package\", \"serviceable\": true, \"sha512\": \"\", \"path\": \"{package.ToLowerInvariant()}/1.0.0\", \"hashPath\": \"\" }}"));
        return $$"""
{
  "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
  "targets": {
    ".NETCoreApp,Version=v10.0": {
      "App/1.0.0": { "dependencies": { {{string.Join(",", packages.Select(package => $"\"{package}\": \"1.0.0\""))}} }, "runtime": { "App.dll": {} } },
      {{targets}}
    }
  },
  "libraries": {
    "App/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
    {{libraries}}
  }
}
""";
    }

    [Fact]
    public void Reachable_NestedTypesOfTwoPackages_NeverJoinThroughTheirNestedName()
    {
        using var fixture = new TempDir();
        var output = System.IO.Path.Combine(fixture.Path, "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        var libP = Emit(output, "LibP", LibPSource);
        var libQ = Emit(output, "LibQ", LibQSource);
        Emit(output, "App", AppSource, libP, libQ);
        File.WriteAllText(System.IO.Path.Combine(output, "App.deps.json"), DepsJsonMarkingPackages("LibP", "LibQ"));

        DataFlowResult result;
        using (DependencyFlowScope.Apply(DependencyFlowMode.Reachable))
        {
            result = DataFlowAnalyzer.Analyze(fixture.Path);
        }

        // The app reaches only LibP, so every LibQ body is pruned: nothing names LibQ and its
        // never-called Cache.Local(string) (a Process.Start sink) leaves no node. Before the
        // qualified keys, `Cache.Local` of one parameter was one string across both packages,
        // so the app's reach of LibP.P.Cache.Local dragged LibQ.Q.Cache.Local(string) in (the
        // note then read 0 of 3: nothing was pruned).
        Assert.DoesNotContain(result.MethodSummaries.Select(summary => summary.Method), symbol => symbol.Contains("LibQ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Nodes, node => (node.Symbol ?? string.Empty).Contains("LibQ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Nodes, node => (node.Symbol ?? string.Empty).Contains("Local(string)", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("1 of 3 method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A member reference through a constructed nested generic type (`Outer.Cache&lt;Gadget&gt;`)
    ///     has a <c>TypeSpecification</c> parent; the qualified chain must decode the generic
    ///     head and meet the open definition the package declares.
    /// </summary>
    [Fact]
    public void Reachable_ConstructedNestedGeneric_TheBodyStaysKept()
    {
        using var fixture = new TempDir();
        var output = System.IO.Path.Combine(fixture.Path, "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        var package = Emit(output, "GenericCache", """
using System;

namespace Gen;

public static class Outer
{
    public class Cache<T>
    {
        public string Describe(T value)
        {
            Console.WriteLine(value?.ToString());
            return value?.ToString() ?? string.Empty;
        }
    }

    public static void Orphan()
    {
        Console.WriteLine("never called");
    }
}
""");
        Emit(output, "App", """
namespace App;

public class Gadget { }

public static class Program
{
    public static void Main(string[] args)
    {
        var cache = new Gen.Outer.Cache<Gadget>();
        _ = cache.Describe(new Gadget());
    }
}
""", package);
        File.WriteAllText(System.IO.Path.Combine(output, "App.deps.json"), DepsJsonMarkingPackages("GenericCache"));

        DataFlowResult result;
        using (DependencyFlowScope.Apply(DependencyFlowMode.Reachable))
        {
            result = DataFlowAnalyzer.Analyze(fixture.Path);
        }

        // The app's call goes through Cache<Gadget>, so the member reference's parent is a
        // TypeSpecification; the generic head decodes, Describe stays kept and only the
        // never-called Orphan is pruned; the constructor of Cache<Gadget> is a third body,
        // reached by the newobj (a decode failure would prune Describe and read 2 of 3).
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("1 of 3 method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The fallback rule: a legacy key unique over the whole definition set keeps a
    ///     secondary entry (a lookup that could not build a qualified chain still matches), a
    ///     legacy key two packages share does not (the collision this issue fixes cannot return
    ///     through the back door).
    /// </summary>
    [Fact]
    public void LegacySecondaryIndex_UniqueKeysStay_CollidedKeysAreDropped()
    {
        using var fixture = new TempDir();
        var output = System.IO.Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(output);
        var libP = Emit(output, "LibP", LibPSource);
        var libQ = Emit(output, "LibQ", LibQSource);

        var index = new DataFlowAnalyzer.DependencyReachIndex();
        using (var peP = new PEReader(File.OpenRead(libP)))
        using (var peQ = new PEReader(File.OpenRead(libQ)))
        {
            index.AddDefinitions(0, peP.GetMetadataReader(), peP);
            index.AddDefinitions(1, peQ.GetMetadataReader(), peQ);
            index.FinalizeLegacyKeys();

            static DataFlowAnalyzer.AssemblyMemberInfo Member(string containingType, string name, int parameters) =>
                new($"{containingType}.{name}", name, containingType, parameters, HasThis: false, ReturnsVoid: false, ReturnType: "string");

            // The collided legacy key (Cache.Local, both packages) has no secondary entry, so a
            // lookup without a qualified chain misses rather than joining both packages.
            var collided = index.Targets(Member("Cache", "Local", 1), DataFlowAnalyzer.DependencyReachIndex.ReachKind.Call, null).ToList();
            Assert.Empty(collided);

            // A unique legacy key (LibP.P.Run) keeps its secondary entry, so a legacy lookup
            // still matches and degrades to today's reach.
            var unique = index.Targets(Member("LibP.P", "Run", 1), DataFlowAnalyzer.DependencyReachIndex.ReachKind.Call, null).ToList();
            Assert.NotEmpty(unique);
            Assert.All(unique, hit => Assert.Equal(0, hit.Item1));
        }
    }

    /// <summary>The qualified chain renders the same string from a definition and from a reference to it, generic heads included.</summary>
    [Fact]
    public void QualifiedContainingType_RendersTheSameChainOnBothSides()
    {
        using var fixture = new TempDir();
        var output = System.IO.Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(output);
        var package = Emit(output, "QualifiedChain", """
namespace Qual;

public static class Outer
{
    public class Cache<T>
    {
        public string Describe(T value) => value?.ToString() ?? string.Empty;
    }
}
""");
        var app = Emit(output, "App", """
namespace App;

public class Gadget { }

public static class Program
{
    public static void Main(string[] args)
    {
        _ = new Qual.Outer.Cache<Gadget>().Describe(new Gadget());
    }
}
""", package);

        // Definition side: the package's Describe method definition token.
        using (var pe = new PEReader(File.OpenRead(package)))
        {
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.MethodDefinitions)
            {
                var definition = reader.GetMethodDefinition(handle);
                if (reader.GetString(definition.Name) == "Describe")
                {
                    Assert.Equal("Qual.Outer.Cache`1", DataFlowAnalyzer.DependencyReachIndex.QualifiedContainingType(reader, System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(handle)));
                }
            }
        }

        // Reference side: the app's member reference to Describe, whose parent is the
        // constructed Cache<Gadget> TypeSpecification, decodes the same chain through the
        // generic head.
        using (var pe = new PEReader(File.OpenRead(app)))
        {
            var reader = pe.GetMetadataReader();
            var matched = false;
            foreach (var handle in reader.MemberReferences)
            {
                if (reader.GetString(reader.GetMemberReference(handle).Name) == "Describe")
                {
                    Assert.Equal("Qual.Outer.Cache`1", DataFlowAnalyzer.DependencyReachIndex.QualifiedContainingType(reader, System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(handle)));
                    matched = true;
                }
            }

            Assert.True(matched, "the app references Describe");
        }
    }
}
