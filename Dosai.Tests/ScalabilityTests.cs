using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Dosai.Tests;

// Issue #65 memory/performance work: the heuristics that changed behavior while keeping the
// output stable at small scale each get a pinning test here - symbol-exact instantiation
// evidence in the dispatch index, the struct call-site key that replaced concatenated key
// strings, and the per-symbol render memoization that dedups id strings across files.
public class ScalabilityTests
{
    private static MethodsSlice ReadMethods(string path)
    {
        var resultJson = Depscan.Dosai.GetMethods(path);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(methodsSlice);
        Assert.NotNull(methodsSlice.CallGraph);
        return methodsSlice!;
    }

    private static List<MethodCalls> DispatchCandidates(MethodsSlice slice, string sourceIdFragment) => slice.MethodCalls!
        .Where(call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynVirtualCandidate &&
                       call.SourceId?.Contains(sourceIdFragment, StringComparison.Ordinal) == true)
        .ToList();

    // Instantiation evidence is symbol-exact. Two types sharing a simple name in different
    // namespaces used to alias onto one RTA flag ("Listener" created marked every Listener
    // instantiated), promoting an unrelated type's overrides to instantiated-evidence rank.
    // The actually-created type ranks rta-candidate; its same-named twin stays cha-candidate.
    [Fact]
    public void GetMethods_SameSimpleNameInTwoNamespaces_OnlyTheInstantiatedTypeRanksRta()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Listeners.cs"), """
public interface IListener
{
    void OnCreated(object source);
}

namespace Telemetry
{
    public class Listener : IListener
    {
        public void OnCreated(object source) { }
    }
}

namespace Diagnostics
{
    // Same simple name as Telemetry.Listener; never instantiated by this tree.
    public class Listener : IListener
    {
        public void OnCreated(object source) { }
    }
}

public static class Boot
{
    public static void Wire(IListener listener)
    {
        var created = new Telemetry.Listener();
        created.OnCreated(created);
        listener.OnCreated("boot");
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);

        var viaInterface = DispatchCandidates(slice, "Boot.Wire")
            .Where(call => call.TargetId?.Contains("Diagnostics.Listener.OnCreated", StringComparison.Ordinal) == true ||
                           call.TargetId?.Contains("Telemetry.Listener.OnCreated", StringComparison.Ordinal) == true)
            .ToList();
        var telemetry = Assert.Single(viaInterface, call => call.TargetId!.Contains("Telemetry.Listener.OnCreated", StringComparison.Ordinal));
        Assert.Equal("rta-candidate", telemetry.DispatchConfidence);
        var diagnostics = Assert.Single(viaInterface, call => call.TargetId!.Contains("Diagnostics.Listener.OnCreated", StringComparison.Ordinal));
        Assert.Equal("cha-candidate", diagnostics.DispatchConfidence);
    }

    // The struct call-site key must collapse exactly what the concatenated key string
    // collapsed: duplicate records for one site - including string instances that are equal
    // but not reference-equal - become one edge, while anything the old key distinguished
    // (call type, evidence kind, position) stays distinct.
    [Fact]
    public void EdgeSiteKey_CollapsesSameSiteAcrossDistinctStringInstances()
    {
        var first = new GraphAssembly.EdgeSiteKey(new string('a', 3) + "1", "Ns.Type.Method", "File.cs", 12, 3, GraphAssembly.CallTypeName(CallType.MethodCall), GraphAssembly.EvidenceKindName(AnalysisEvidenceKind.SourceRoslynDirect));
        var duplicate = new GraphAssembly.EdgeSiteKey(new string('a', 3) + "1", "Ns.Type." + "Method", "File" + ".cs", 12, 3, GraphAssembly.CallTypeName(CallType.MethodCall), GraphAssembly.EvidenceKindName(AnalysisEvidenceKind.SourceRoslynDirect));
        Assert.Equal(first, duplicate);
        Assert.Equal(first.GetHashCode(), duplicate.GetHashCode());

        var otherLine = new GraphAssembly.EdgeSiteKey("aaa1", "Ns.Type.Method", "File.cs", 13, 3, GraphAssembly.CallTypeName(CallType.MethodCall), GraphAssembly.EvidenceKindName(AnalysisEvidenceKind.SourceRoslynDirect));
        var otherKind = new GraphAssembly.EdgeSiteKey("aaa1", "Ns.Type.Method", "File.cs", 12, 3, GraphAssembly.CallTypeName(CallType.MethodCall), GraphAssembly.EvidenceKindName(AnalysisEvidenceKind.SourceRoslynVirtualCandidate));
        var otherType = new GraphAssembly.EdgeSiteKey("aaa1", "Ns.Type.Method", "File.cs", 12, 3, GraphAssembly.CallTypeName(CallType.ConstructorCall), GraphAssembly.EvidenceKindName(AnalysisEvidenceKind.SourceRoslynDirect));
        Assert.NotEqual(first, otherLine);
        Assert.NotEqual(first, otherKind);
        Assert.NotEqual(first, otherType);
    }

    // The enum-name tables back the key's tag legs; they must stay identical to what enum
    // interpolation produced, or dedup silently changes meaning when a value is added.
    [Fact]
    public void GraphAssembly_NameTables_MatchEnumToString()
    {
        foreach (var value in Enum.GetValues<CallType>())
        {
            Assert.Equal(value.ToString(), GraphAssembly.CallTypeName(value));
        }

        foreach (var value in Enum.GetValues<AnalysisEvidenceKind>())
        {
            Assert.Equal(value.ToString(), GraphAssembly.EvidenceKindName(value));
        }
    }

    // The render memo returns one instance per symbol so call sites, inventories, nodes and
    // edges can share the same string object instead of one copy per mention.
    [Fact]
    public void SourceRenderCache_ReturnsTheSameStringInstancePerSymbol()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace N { public class C { public void M(int a) { } } }");
        var compilation = CSharpCompilation.Create(
            "RenderCacheTest",
            syntaxTrees: [tree],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree);
        var method = model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single())!;

        var cache = new SourceRenderCache();
        Assert.Same(cache.Signature(method), cache.Signature(method));
        Assert.Same(cache.Display(method.ContainingNamespace), cache.Display(method.ContainingNamespace));
        Assert.Equal(cache.Signature(method), Depscan.Dosai.FormatMethodSignature(method));
    }

    // Parse options resolve from the scan root, not each file's directory: a project rooted at
    // the scan path decides the preprocessor symbols for every file below it, so a
    // representative net8.0 build compiles `#if NET8_0` arms in subdirectories too (the
    // per-directory fallback used to inherit the latest-modern-net define set there).
    [Fact]
    public void GetMethods_GuardsInSubdirectories_ResolveAgainstTheScanRootProject()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "App.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "Generated"));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Generated", "Guards.cs"), """
public static class Guards
{
#if NET8_0
    public static void Net8Arm() { }
#endif
#if NET10_0
    public static void Net10Arm() { }
#endif
}
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.Contains(slice.Methods!, method => method is { ClassName: "Guards", Name: "Net8Arm" });
        Assert.DoesNotContain(slice.Methods!, method => method is { ClassName: "Guards", Name: "Net10Arm" });
    }

    // A single-file scan reads its project context from the file's directory. Resolving the
    // parse options from the file path itself enumerated no project file and evaluated a
    // net48 project's guards against the latest-modern-net fallback.
    [Fact]
    public void GetMethods_SingleFileScan_ResolvesGuardsAgainstItsOwnProject()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Legacy.csproj"), """
<Project>
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
</Project>
""");
        var file = Path.Combine(tempDirectory.Path, "LegacyCode.cs");
        File.WriteAllText(file, """
public static class LegacyCode
{
#if NETFRAMEWORK
    public static void FrameworkOnly() { }
#else
    public static void NotFramework() { }
#endif
}
""");

        var slice = ReadMethods(file);

        Assert.Contains(slice.Methods!, method => method is { ClassName: "LegacyCode", Name: "FrameworkOnly" });
        Assert.DoesNotContain(slice.Methods!, method => method is { ClassName: "LegacyCode", Name: "NotFramework" });
        Assert.Equal(new[] { "net48" }, slice.Metadata!.TargetFrameworks);
    }

    // The dispatch index's instantiation scan binds each creation's enclosing statement, which
    // for a creation heading a fluent chain is the whole chain - super-linear to bind. Members
    // the depth guard keeps from the operation factory stay out of the scan as well, so a type
    // created only inside such a member is not instantiation evidence (the call-graph walker
    // never analyzes that member either) and ranks cha-candidate.
    [Fact]
    public void GetMethods_DispatchIndex_SkipsCreationsInMembersTheDepthGuardSkips()
    {
        using var tempDirectory = new TemporaryDirectory();
        var chain = "new OnlyInDeep()" + string.Concat(Enumerable.Repeat(".M()", 150));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Deep.cs"), $$"""
public interface ISvc { void Run(); }
public class OnlyInDeep : ISvc { public void Run() { } public OnlyInDeep M() => this; }
public class Plain : ISvc { public void Run() { } }
public static class Deep
{
    public static object Chain() => {{chain}};
}
public static class Boot
{
    public static void Go(ISvc service)
    {
        var plain = new Plain();
        service.Run();
    }
}
""");

        using (OperationDepthGuard.OverrideMaxSyntaxDepth(200))
        {
            var slice = ReadMethods(tempDirectory.Path);

            Assert.Contains(slice.Diagnostics!, diagnostic => diagnostic.Contains("Deep.cs", StringComparison.Ordinal) && diagnostic.Contains("nest deeper than 200 syntax levels", StringComparison.Ordinal));
            var candidates = DispatchCandidates(slice, "Boot.Go");
            Assert.Equal("rta-candidate", Assert.Single(candidates, call => call.TargetId!.StartsWith("Plain.Run", StringComparison.Ordinal)).DispatchConfidence);
            Assert.Equal("cha-candidate", Assert.Single(candidates, call => call.TargetId!.StartsWith("OnlyInDeep.Run", StringComparison.Ordinal)).DispatchConfidence);
        }
    }

    // The created type is instantiation evidence even when its constructor call fails to bind -
    // an argument of an unresolved type, routine in unrestored trees. The operation form of the
    // scan saw an IInvalidOperation there and missed the creation.
    [Fact]
    public void GetMethods_CreationWithUnboundConstructorArgument_CountsAsInstantiated()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Widen.cs"), """
namespace W
{
    public interface ISvc { void Run(); }
    public class Created : ISvc { public void Run() { } }
    public class BrokenCtorArg : ISvc { public BrokenCtorArg(Missing.Type missing) { } public void Run() { } }
    public class NeverCreated : ISvc { public void Run() { } }
    public static class Boot
    {
        public static void Go(ISvc service, object value)
        {
            var created = new Created();
            var broken = new BrokenCtorArg(value);
            service.Run();
        }
    }
}
""");

        var candidates = DispatchCandidates(ReadMethods(tempDirectory.Path), "W.Boot.Go");

        Assert.Equal("rta-candidate", Assert.Single(candidates, call => call.TargetId!.StartsWith("W.Created.Run", StringComparison.Ordinal)).DispatchConfidence);
        Assert.Equal("rta-candidate", Assert.Single(candidates, call => call.TargetId!.StartsWith("W.BrokenCtorArg.Run", StringComparison.Ordinal)).DispatchConfidence);
        Assert.Equal("cha-candidate", Assert.Single(candidates, call => call.TargetId!.StartsWith("W.NeverCreated.Run", StringComparison.Ordinal)).DispatchConfidence);
    }

    // The memory contract of issue #65: the source compilations (and through them every syntax
    // tree) are unreachable once source and framework analysis return, before the IL call
    // graph, enrichment, reachability and serialization. Reassigning a local in the slice
    // builder did not release them - a captured closure and Tier-0 liveness kept them alive -
    // so the contract is checked on the objects themselves.
    [Fact]
    public void GetMethodsSlice_ReleasesSourceCompilationsBeforeTheIlPhase()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "App.cs"), """
public class Handler
{
    public void Handle() => System.Console.WriteLine("handled");
}
""");

        Depscan.Dosai.ReleaseProbe probe;
        using (Depscan.Dosai.ObserveCompilationRelease(out probe))
        {
            Depscan.Dosai.GetMethodsSlice(tempDirectory.Path);
        }

        Assert.True(probe.Observed > 0);
        Assert.Equal(0, probe.AliveAfterRelease);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-scalability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

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
}
