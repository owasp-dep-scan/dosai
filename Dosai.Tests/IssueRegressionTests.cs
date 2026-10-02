using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Dosai.Tests;

// Regression tests written against the reported reproductions (locale tests for #63 live in
// LocaleInvarianceTests.cs):
// - #64 the Roslyn InvalidOperationException on a generic method over a generic interface
// - #65 symbol analysis on one core and super-linear in the file count (parallel workers,
//   bucketed dispatch, memoized lookups, byte-identical output to the sequential path)
// Partial class, same xunit collection as the rest of DosaiTests: these tests flip the static
// worker knob and redirect the console, which is only safe serialized against the other
// console-redirecting tests.
public partial class DosaiTests
{
    private static readonly JsonSerializerOptions JsonStringEnums = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Runs <paramref name="scan" /> with the symbol-analysis worker count pinned.</summary>
    private static T WithSymbolAnalysisWorkers<T>(int workers, Func<T> scan)
    {
        var previous = Depscan.Dosai.MaxSymbolAnalysisWorkers;
        try
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = workers;
            return scan();
        }
        finally
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = previous;
        }
    }

    private static List<MethodCalls> DispatchCandidates(MethodsSlice slice, string sourceIdFragment) => slice.MethodCalls!
        .Where(call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynVirtualCandidate &&
                       call.SourceId?.Contains(sourceIdFragment, StringComparison.Ordinal) == true)
        .ToList();

    #region Issue #64 - Roslyn failure on generic method over a generic interface

    [Fact]
    public void Roslyn_FindImplementationForInterfaceMember_ThrowsOnConstructedGenericMethod()
    {
        // Pins the root cause. The API takes the member as its interface declares it; handed the
        // method constructed with a call's type arguments, Roslyn constructs it again while
        // matching the implementation and MethodSymbol.Construct throws. ConstructedFrom - the
        // same member of the same constructed interface - resolves the implementation.
        var compilation = CSharpCompilation.Create(
            "Issue64",
            [CSharpSyntaxTree.ParseText("""
public interface IBuilder<TOwner> { IBuilder<TOwner> Join<TEntity>(); }
public class Thing { }
public class Builder : IBuilder<Thing> { public IBuilder<Thing> Join<TEntity>() => this; }
""")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var builder = compilation.GetTypeByMetadataName("Builder")!;
        var constructedInterface = builder.AllInterfaces.Single();
        var join = constructedInterface.GetMembers("Join").OfType<IMethodSymbol>().Single();
        var joinOfInt = join.Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        Assert.Throws<InvalidOperationException>(() => builder.FindImplementationForInterfaceMember(joinOfInt));
        var implementation = Assert.IsAssignableFrom<IMethodSymbol>(builder.FindImplementationForInterfaceMember(joinOfInt.ConstructedFrom));
        Assert.Equal("Builder", implementation.ContainingType.Name);
        Assert.Null(builder.FindImplementationForInterfaceMember(joinOfInt.OriginalDefinition));
    }

    [Fact]
    public void GetMethods_GenericMethodOnGenericInterface_ResolvesTheImplementation()
    {
        // The 21-line reproduction from the issue plus the variant the reporter says throws the
        // same way, with the call in a separate class. Before the fix `methods` exited 1 with no
        // output; containment alone would have dropped the dispatch edge to Builder.Join.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "GenericInterfaceRepro.cs"), """
namespace Repro;

public interface IBuilder<TOwner>
{
    IBuilder<TOwner> Join<TEntity>();
}

public class Thing
{
}

public class Builder : IBuilder<Thing>
{
    public IBuilder<Thing> Inner { get; set; }

    public IBuilder<Thing> Join<TEntity>()
    {
        this.Inner.Join<TEntity>();
        return this;
    }
}

public class Caller
{
    public void Run(IBuilder<Thing> builder)
    {
        builder.Join<int>();
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.Contains(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynDirect } &&
            call.SourceId?.StartsWith("Repro.Builder.Join", StringComparison.Ordinal) == true &&
            call.TargetId?.StartsWith("Repro.IBuilder<TOwner>.Join", StringComparison.Ordinal) == true);
        var candidate = Assert.Single(DispatchCandidates(slice, "Repro.Caller.Run"));
        Assert.StartsWith("Repro.Builder.Join", candidate.TargetId, StringComparison.Ordinal);
        Assert.Equal("cha-candidate", candidate.DispatchConfidence);
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.StartsWith("Dispatch resolution failed", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMethods_StructImplementingGenericInterfaceMethod_ResolvesTheImplementation()
    {
        // Second shape from the issue thread (dotnet/runtime JIT test trees): a struct
        // implementing a generic method declared on a generic interface, called through the
        // interface.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "StructGenericInterfaceRepro.cs"), """
internal interface IBase<T>
{
    string Foo<U>();
}

internal struct DerivedStructString : IBase<string>
{
    public string Foo<U>() => "value";
}

internal static class CallSite
{
    internal static string Call(IBase<string> receiver) => receiver.Foo<int>();
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        var candidate = Assert.Single(DispatchCandidates(slice, "CallSite.Call"));
        Assert.StartsWith("DerivedStructString.Foo", candidate.TargetId, StringComparison.Ordinal);
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.StartsWith("Dispatch resolution failed", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMethods_GenericMethodOnGenericInterface_RanksInstantiatedImplementerFirst()
    {
        // Healthy generic-over-generic dispatch with RTA evidence: the instantiated implementer
        // of IRepository<Order>.Find<TKey> ranks rta-candidate ahead of the uninstantiated one.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Repositories.cs"), """
public interface IRepository<TEntity>
{
    TEntity Find<TKey>(TKey key);
}

public class Order
{
}

public class SqlOrders : IRepository<Order>
{
    public Order Find<TKey>(TKey key) => new Order();
}

public class CachedOrders : IRepository<Order>
{
    public Order Find<TKey>(TKey key) => new Order();
}

public static class OrderService
{
    public static Order Load(int id)
    {
        IRepository<Order> repository = new SqlOrders();
        return repository.Find(id);
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        var candidates = DispatchCandidates(slice, "OrderService.Load");
        Assert.Equal(new[] { "rta-candidate", "cha-candidate" }, candidates.Select(call => call.DispatchConfidence));
        Assert.StartsWith("SqlOrders.Find", candidates[0].TargetId, StringComparison.Ordinal);
        Assert.StartsWith("CachedOrders.Find", candidates[1].TargetId, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethods_FailingDispatchResolution_IsContainedAndReportedDeterministically()
    {
        // Containment for shapes Roslyn may still fail on: the failing candidate is dropped,
        // the call keeps its direct edge and its other candidates, and the slice diagnostics
        // count the affected call sites. The count is per call site in file order, so it and
        // the rest of the output are identical for any worker count.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Handlers.cs"), """
public interface IHandler
{
    void Handle(string message);
}

public class Healthy : IHandler
{
    public void Handle(string message) { }
}

public class Broken : IHandler
{
    public void Handle(string message) { }
}
""");
        for (var index = 0; index < 6; index++)
        {
            File.WriteAllText(Path.Combine(tempDirectory.Path, $"Dispatcher{index}.cs"), $$"""
public static class Dispatcher{{index}}
{
    public static void First(IHandler handler) => handler.Handle("first");

    public static void Second(IHandler handler) => handler.Handle("second");
}
""");
        }

        string Scan(int workers)
        {
            using var hook = DispatchResolver.SourceIndex.OverrideBeforeInterfaceResolution((type, _) =>
            {
                if (type.Name == "Broken")
                {
                    throw new InvalidOperationException("injected");
                }
            });
            return WithSymbolAnalysisWorkers(workers, () => Depscan.Dosai.GetMethods(tempDirectory.Path));
        }

        var sequential = Scan(1);
        var parallel = Scan(4);
        Assert.Equal(NormalizeGeneratedAt(sequential), NormalizeGeneratedAt(parallel));

        var slice = JsonSerializer.Deserialize<MethodsSlice>(parallel, JsonStringEnums)!;
        // Twelve call sites share one memoized lookup; every one of them is counted.
        var diagnostic = Assert.Single(slice.Diagnostics!, entry => entry.StartsWith("Dispatch resolution failed", StringComparison.Ordinal));
        Assert.StartsWith("Dispatch resolution failed at 12 call site(s), first ", diagnostic, StringComparison.Ordinal);
        // "First" is the first affected call site in scan order; which dispatcher file that is
        // depends on directory enumeration, but never on the worker count (asserted above).
        Assert.Matches(@"Dispatcher\d\.cs:3 \('IHandler\.Handle\(string\)' on 'Broken', InvalidOperationException\);", diagnostic);
        var candidates = slice.MethodCalls!.Where(call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynVirtualCandidate).ToList();
        Assert.Equal(12, candidates.Count);
        Assert.All(candidates, call => Assert.StartsWith("Healthy.Handle", call.TargetId, StringComparison.Ordinal));
        Assert.Equal(12, slice.MethodCalls!.Count(call => call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynDirect } && call.TargetId?.StartsWith("IHandler.Handle", StringComparison.Ordinal) == true));
    }

    #endregion

    #region Issue #65 - parallel symbol analysis and sub-linear dispatch

    [Fact]
    public void GetMethods_ParallelSymbolAnalysis_ProducesByteIdenticalOutput()
    {
        using var tempDirectory = new TemporaryDirectory();
        for (var index = 0; index < 12; index++)
        {
            File.WriteAllText(Path.Combine(tempDirectory.Path, $"Gen{index}.cs"), $$"""
using System;

namespace Gen{{index}}
{
    public interface IShape{{index}}<T>
    {
        int Area<U>(U scale);
    }

    public class Circle{{index}} : IShape{{index}}<int>
    {
        private readonly int radius;

        public Circle{{index}}(int radius)
        {
            this.radius = radius;
        }

        public int Area<U>(U scale) => radius;

        public virtual string Describe() => "circle";
    }

    public sealed class SealedCircle{{index}} : Circle{{index}}
    {
        public SealedCircle{{index}}(int radius) : base(radius)
        {
        }

        public override string Describe() => "sealed";
    }

    public static class Entry{{index}}
    {
        public static int Run(string[] args)
        {
            IShape{{index}}<int> shape = new Circle{{index}}(int.Parse(args[0]));
            var area = shape.Area<string>("2");
            Circle{{index}} circle = new SealedCircle{{index}}(3);
            var text = circle.Describe();
            Console.WriteLine(area + text.Length);
            return area;
        }
    }
}
""");
        }

        File.WriteAllText(Path.Combine(tempDirectory.Path, "GenVb1.vb"), """
Namespace GenVb
    Public Class VbShape1
        Public Function Twice(value As Integer) As Integer
            Return value * 2
        End Function
    End Class
End Namespace
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "GenVb2.vb"), """
Namespace GenVb
    Public Class VbShape2
        Public Function Thrice(value As Integer) As Integer
            Return value * 3
        End Function
    End Class
End Namespace
""");

        var sequential = WithSymbolAnalysisWorkers(1, () => Depscan.Dosai.GetMethods(tempDirectory.Path));
        var parallel = WithSymbolAnalysisWorkers(6, () => Depscan.Dosai.GetMethods(tempDirectory.Path));

        // Per-file results merge in file order and the dispatch index unions per-tree evidence,
        // so the worker count can never reorder methods, calls, or edges.
        Assert.Equal(NormalizeGeneratedAt(sequential), NormalizeGeneratedAt(parallel));

        // The tree must exercise the interesting paths, so the equality above is not vacuous:
        // generic-over-generic interface dispatch (issue #64's shape), RTA ranking from the
        // parallel object-creation scan, class-hierarchy dispatch, and VB members.
        var slice = JsonSerializer.Deserialize<MethodsSlice>(parallel, JsonStringEnums);
        Assert.NotNull(slice);
        Assert.True(slice.Methods!.Count >= 60, $"expected a substantive inventory, got {slice.Methods.Count}");
        for (var index = 0; index < 12; index++)
        {
            var candidates = DispatchCandidates(slice, $"Gen{index}.Entry{index}.Run");
            Assert.Contains(candidates, call => call.DispatchConfidence == "rta-candidate" && call.TargetId?.StartsWith($"Gen{index}.Circle{index}.Area", StringComparison.Ordinal) == true);
            Assert.Contains(candidates, call => call.DispatchConfidence == "rta-candidate" && call.TargetId?.StartsWith($"Gen{index}.SealedCircle{index}.Describe", StringComparison.Ordinal) == true);
        }

        Assert.Contains(slice.Methods!, method => method.Name == "Twice");
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.StartsWith("Dispatch resolution failed", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMethods_ParallelSymbolAnalysis_ReportsWorkerCountInDebugLog()
    {
        using var tempDirectory = new TemporaryDirectory();
        for (var index = 0; index < 4; index++)
        {
            File.WriteAllText(Path.Combine(tempDirectory.Path, $"Workers{index}.cs"), $$"""
public static class Workers{{index}}
{
    public static int Combine(int value) => value + {{index}};
}
""");
        }

        var recorder = new LineRecorder();
        WithSymbolAnalysisWorkers(3, () =>
        {
            lock (ConsoleOutputLock)
            {
                var originalError = Console.Error;
                Console.SetError(recorder);
                try
                {
                    DebugLog.Configure(true);
                    return Depscan.Dosai.GetMethods(tempDirectory.Path);
                }
                finally
                {
                    DebugLog.Configure(false);
                    Console.SetError(originalError);
                }
            }
        });

        var lines = recorder.Snapshot();
        Assert.Contains(lines, line => line.Contains("symbol analysis workers: 3", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("start methods.dispatch-index", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("call sites with a failed dispatch resolution: 0", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMethods_InterfaceDispatch_RanksInstantiatedImplementerFirst()
    {
        // Bucketed candidate lookup must reproduce the previous ranking semantics: the type the
        // program instantiates is an rta-candidate and outranks the uninstantiated cha-candidate.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Ranking.cs"), """
public interface ITransport
{
    void Send(string payload);
}

public class HttpTransport : ITransport
{
    public void Send(string payload) { }
}

public class QueueTransport : ITransport
{
    public void Send(string payload) { }
}

public static class RankingEntry
{
    public static void Main()
    {
        ITransport transport = new HttpTransport();
        transport.Send("payload");
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        var candidates = DispatchCandidates(slice, "RankingEntry.Main");
        Assert.Equal(new[] { "rta-candidate", "cha-candidate" }, candidates.Select(call => call.DispatchConfidence));
        Assert.StartsWith("HttpTransport.Send", candidates[0].TargetId, StringComparison.Ordinal);
        Assert.StartsWith("QueueTransport.Send", candidates[1].TargetId, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethods_TypeImplementingTwoConstructionsOfOneInterface_YieldsEachCandidateOnce()
    {
        // Multi implements IHandler<int> and IHandler<string>, which share one original
        // definition. Bucketing per construction visited the type twice per lookup and emitted
        // the same candidate edge twice.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Multi.cs"), """
public interface IHandler<T>
{
    void Handle(T value);
}

public class Multi : IHandler<int>, IHandler<string>
{
    public void Handle(int value) { }
    public void Handle(string value) { }
}

public static class Callers
{
    public static void Numbers(IHandler<int> handler) => handler.Handle(1);

    public static void Texts(IHandler<string> handler) => handler.Handle("one");
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        Assert.Equal("Multi.Handle(int):void", Assert.Single(DispatchCandidates(slice, "Callers.Numbers")).TargetId);
        Assert.Equal("Multi.Handle(string):void", Assert.Single(DispatchCandidates(slice, "Callers.Texts")).TargetId);
    }

    [Fact]
    public void GetMethods_MegamorphicCallSite_KeepsSixteenCandidatesInstantiatedFirst()
    {
        // A call with many implementers keeps a capped candidate list, instantiated types first
        // and the rest in type order. (Before the rework a call with 32 or more candidates kept
        // only the first one: the cap compared the total candidate count, not the number
        // already returned.)
        using var tempDirectory = new TemporaryDirectory();
        var source = new System.Text.StringBuilder("public interface IShape { double Area(); }\n");
        for (var index = 0; index < 40; index++)
        {
            source.Append(CultureInfo.InvariantCulture, $"public class Shape{index:00} : IShape {{ public double Area() => {index}; }}\n");
        }

        source.Append("public static class Use { public static double Sum(IShape shape) => shape.Area(); public static IShape Make() => new Shape39(); }\n");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Shapes.cs"), source.ToString());

        var slice = ReadMethods(tempDirectory.Path);
        var candidates = DispatchCandidates(slice, "Use.Sum");
        Assert.Equal(16, candidates.Count);
        Assert.Equal("Shape39.Area():double", candidates[0].TargetId);
        Assert.Equal("rta-candidate", candidates[0].DispatchConfidence);
        Assert.Equal(
            Enumerable.Range(0, 15).Select(index => $"Shape{index:00}.Area():double"),
            candidates.Skip(1).Select(call => call.TargetId));
        Assert.All(candidates.Skip(1), call => Assert.Equal("cha-candidate", call.DispatchConfidence));
    }

    [Fact]
    public void GetMethods_BaseCall_SynthesizesNoDispatchCandidates()
    {
        // `base.Describe()` is a non-virtual call: it runs ShapeBase.Describe and never the
        // override, so it must not get a dispatch-candidate edge to SealedShape.Describe. A
        // virtual call through a base-typed receiver still resolves the override.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Shapes.cs"), """
public class ShapeBase
{
    public virtual string Describe() => "base";
}

public sealed class SealedShape : ShapeBase
{
    public override string Describe() => "sealed";

    public string DescribeViaBase() => base.Describe();
}

public static class ShapeEntry
{
    public static string Main()
    {
        ShapeBase shape = new SealedShape();
        return shape.Describe();
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        Assert.Contains(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynDirect } &&
            call.SourceId?.StartsWith("SealedShape.DescribeViaBase", StringComparison.Ordinal) == true &&
            call.TargetId?.StartsWith("ShapeBase.Describe", StringComparison.Ordinal) == true);
        Assert.Empty(DispatchCandidates(slice, "SealedShape.DescribeViaBase"));
        var candidate = Assert.Single(DispatchCandidates(slice, "ShapeEntry.Main"));
        Assert.Equal("SealedShape.Describe():string", candidate.TargetId);
        Assert.Equal("rta-candidate", candidate.DispatchConfidence);
    }

    [Fact]
    public void GetMethods_UpperCaseSourceExtension_IsAnalyzed()
    {
        // Discovery and parsing match extensions case-insensitively; the per-file loop compared
        // them ordinally, so a Program.CS was parsed into the compilation but its members never
        // reached the inventory.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Shouting.CS"), """
public static class Shouting
{
    public static string Loud(string value) => value.ToUpperInvariant();
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        Assert.Contains(slice.Methods!, method => method.Name == "Loud");
        Assert.Contains(slice.MethodCalls!, call => call.SourceId?.StartsWith("Shouting.Loud", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DedicatedStackForEach_RunsEveryIndexOnceOnAnalysisThreads()
    {
        foreach (var workers in new[] { 1, 3, 16 })
        {
            var visits = new int[50];
            var offAnalysisThread = 0;
            var depthLimits = new ConcurrentBag<int>();
            using (OperationDepthGuard.OverrideMaxSyntaxDepth(1234))
            using (new CultureScope("tr-TR"))
            {
                DedicatedStack.Run("test coordinator", () =>
                {
                    DedicatedStack.ForEach("test worker", workers, visits.Length, index =>
                    {
                        Interlocked.Increment(ref visits[index]);
                        if (!DedicatedStack.IsOnAnalysisThread)
                        {
                            Interlocked.Increment(ref offAnalysisThread);
                        }

                        // Ambient scopes and the culture reach every worker.
                        depthLimits.Add(OperationDepthGuard.MaxSyntaxDepth);
                        Assert.Equal("tr-TR", CultureInfo.CurrentCulture.Name);
                    });
                    return 0;
                });
            }

            Assert.All(visits, count => Assert.Equal(1, count));
            Assert.Equal(0, offAnalysisThread);
            Assert.All(depthLimits, limit => Assert.Equal(1234, limit));
        }

        var called = false;
        DedicatedStack.ForEach("test empty", 4, 0, _ => called = true);
        Assert.False(called);
    }

    [Fact]
    public void DedicatedStackForEach_RethrowsTheLowestFailingIndexLikeASequentialLoop()
    {
        foreach (var workers in new[] { 1, 4 })
        {
            var started = new ConcurrentBag<int>();
            var failure = Assert.Throws<InvalidOperationException>(() =>
                DedicatedStack.ForEach("test failures", workers, 40, index =>
                {
                    started.Add(index);
                    if (index is 7 or 23)
                    {
                        throw new InvalidOperationException(index.ToString(CultureInfo.InvariantCulture));
                    }
                }));

            Assert.Equal("7", failure.Message);
            // Everything a sequential loop would have run before failing did run.
            Assert.All(Enumerable.Range(0, 8), index => Assert.Contains(index, started));
            if (workers == 1)
            {
                Assert.Equal(Enumerable.Range(0, 8), started.Order());
            }
        }
    }

    #endregion

    #region Issue #70 - per-edge key strings in CollapseDuplicateCallSites

    /// <summary>
    ///     The old grouping key, kept verbatim as the reference the allocation-free comparison
    ///     must reproduce: same groups, same order, including the separator's ordering trap for
    ///     ids that contain characters below U+001F.
    /// </summary>
    private static string CollapseKeyReference(MethodCallEdge edge) =>
        $"{edge.SourceId}\u001f{edge.TargetId}\u001f{edge.CallType}\u001f{edge.EvidenceKind}";

    private static List<MethodCallEdge> CollapseReference(CallGraph callGraph)
    {
        var collapsed = new List<MethodCallEdge>();
        foreach (var group in callGraph.Edges
                     .GroupBy(CollapseKeyReference, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var edges = group.ToList();
            var keeper = edges
                .OrderBy(edge => edge.CallLocation?.FileName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(edge => edge.CallLocation?.LineNumber ?? 0)
                .ThenBy(edge => edge.CallLocation?.ColumnNumber ?? 0)
                .First();
            keeper.CallSiteCount = edges
                .Select(edge => $"{edge.CallLocation?.FileName}:{edge.CallLocation?.LineNumber}:{edge.CallLocation?.ColumnNumber}")
                .Distinct(StringComparer.Ordinal)
                .Count();
            keeper.Arguments = edges.SelectMany(edge => edge.Arguments ?? []).Distinct(StringComparer.Ordinal).ToList();
            keeper.ArgumentExpressions = edges.SelectMany(edge => edge.ArgumentExpressions ?? []).Distinct(StringComparer.Ordinal).ToList();
            keeper.Evidence = edges.SelectMany(edge => edge.Evidence)
                .DistinctBy(evidence => (evidence.Kind, evidence.Source, evidence.Description, evidence.FileName, evidence.LineNumber, evidence.ColumnNumber))
                .ToList();
            collapsed.Add(keeper);
        }

        return collapsed;
    }

    private static void AssertSameCollapse(List<MethodCallEdge> expected, CallGraph actual)
    {
        Assert.Equal(expected.Count, actual.Edges.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var want = expected[i];
            var got = actual.Edges[i];
            // The same input edge must survive as the keeper: same original id, location, fields.
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.SourceId, got.SourceId);
            Assert.Equal(want.TargetId, got.TargetId);
            Assert.Equal(want.CallType, got.CallType);
            Assert.Equal(want.EvidenceKind, got.EvidenceKind);
            Assert.Equal(want.CallSiteCount, got.CallSiteCount);
            Assert.Equal(want.CallLocation?.FileName, got.CallLocation?.FileName);
            Assert.Equal(want.CallLocation?.LineNumber, got.CallLocation?.LineNumber);
            Assert.Equal(want.CallLocation?.ColumnNumber, got.CallLocation?.ColumnNumber);
            Assert.Equal(want.Arguments, got.Arguments);
            Assert.Equal(want.ArgumentExpressions, got.ArgumentExpressions);
            Assert.Equal(want.Evidence.Count, got.Evidence.Count);
            for (var e = 0; e < want.Evidence.Count; e++)
            {
                Assert.Same(want.Evidence[e], got.Evidence[e]);
            }
        }
    }

    private static MethodCallEdge CloneEdge(MethodCallEdge edge) => new()
    {
        Id = edge.Id,
        SourceId = edge.SourceId,
        TargetId = edge.TargetId,
        CallType = edge.CallType,
        EvidenceKind = edge.EvidenceKind,
        CallLocation = edge.CallLocation,
        Arguments = edge.Arguments,
        ArgumentExpressions = edge.ArgumentExpressions,
        Evidence = [.. edge.Evidence],
    };

    [Fact]
    public void CollapseDuplicateCallSites_MatchesTheConcatenatedKeyReference()
    {
        MethodCallEdge Edge(string src, string tgt, CallType type, AnalysisEvidenceKind kind, string file, int line, int col, int id,
            string[]? arguments = null, string[]? expressions = null, (AnalysisEvidenceKind Kind, string Description, string Confidence, int Line)[]? evidence = null)
        => new()
        {
            Id = $"e{id}",
            SourceId = src,
            TargetId = tgt,
            CallType = type,
            EvidenceKind = kind,
            CallLocation = new CallLocation { FileName = file, LineNumber = line, ColumnNumber = col },
            Arguments = arguments is null ? null : [.. arguments],
            ArgumentExpressions = expressions is null ? null : [.. expressions],
            Evidence = evidence?.Select(e => new AnalysisEvidence
            {
                Kind = e.Kind,
                Source = "test",
                Description = e.Description,
                Confidence = e.Confidence,
                FileName = file,
                LineNumber = e.Line,
            }).ToList() ?? [],
        };

        var graph = new CallGraph
        {
            Edges =
            [
                // Same pair, three sites, two distinct: count 2, keeper minimal by (file, line, col).
                Edge("Ns.A.Run():void", "Ns.B.Save(string):void", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynDirect, "/src/B.cs", 10, 3, 1, ["b", "a"], ["x"]),
                Edge("Ns.A.Run():void", "Ns.B.Save(string):void", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynDirect, "/src/A.cs", 90, 9, 2, ["a", "c"], ["y", "x"]),
                Edge("Ns.A.Run():void", "Ns.B.Save(string):void", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynDirect, "/src/A.cs", 90, 9, 3),
                // Same pair, other call type: a separate fact that stays a separate edge.
                Edge("Ns.A.Run():void", "Ns.B.Save(string):void", CallType.ConstructorCall, AnalysisEvidenceKind.SourceRoslynDirect, "/src/A.cs", 1, 1, 4, arguments: ["z"]),
                // Tie on (file, line, col): the first in encounter order is the keeper.
                Edge("Ns.C.X():void", "Ns.D.Y():void", CallType.MethodCall, AnalysisEvidenceKind.AssemblyIlDirect, "/src/T.cs", 5, 5, 5, ["first"]),
                Edge("Ns.C.X():void", "Ns.D.Y():void", CallType.MethodCall, AnalysisEvidenceKind.AssemblyIlDirect, "/src/T.cs", 5, 5, 6, ["second"]),
                // Duplicates inside one edge's own lists are deduped even in a singleton group,
                // and the lists are empty (never null) after collapsing.
                Edge("Ns.E.One():void", "Ns.F.Two():void", CallType.PropertyGet, AnalysisEvidenceKind.SourceUnresolved, "/src/U.cs", 2, 8, 7,
                    ["dup", "dup"], ["e1", "e2", "e1"],
                    [(AnalysisEvidenceKind.SourceUnresolved, "unresolved call", "Low", 1), (AnalysisEvidenceKind.SourceUnresolved, "unresolved call", "High", 1)]),
                // Evidence merging: the same tuple from two edges keeps the first occurrence.
                Edge("Ns.G.M():void", "Ns.H.N():void", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynVirtualCandidate, "/src/V.cs", 3, 1, 8,
                    evidence: [(AnalysisEvidenceKind.SourceRoslynVirtualCandidate, "candidate", "Medium", 3)]),
                Edge("Ns.G.M():void", "Ns.H.N():void", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynVirtualCandidate, "/src/W.cs", 4, 1, 9,
                    evidence: [(AnalysisEvidenceKind.SourceRoslynVirtualCandidate, "candidate", "High", 3), (AnalysisEvidenceKind.SourceRoslynVirtualCandidate, "other", "Low", 4)]),
                // Null argument lists and a null call location keep the old defensive shape.
                Edge("Ns.I.J():void", "Ns.J.K():void", CallType.MethodCall, AnalysisEvidenceKind.ExternalSummary, "/src/Z.cs", 7, 2, 10),
                Edge("Ns.I.J():void", "Ns.J.K():void", CallType.MethodCall, AnalysisEvidenceKind.ExternalSummary, "/src/Z.cs", 7, 2, 11),
            ]
        };
        graph.Edges[10].CallLocation = null!;

        var expected = CollapseReference(new CallGraph { Edges = [.. graph.Edges.Select(CloneEdge)] });

        ReachabilityAnalyzer.CollapseDuplicateCallSites(graph);

        AssertSameCollapse(expected, graph);
    }

    [Fact]
    public void CompareCollapseEdges_ReproducesConcatenatedKeyOrderOnControlCharIds()
    {
        // Ids that make field-by-field comparison diverge from the concatenated key: strict
        // prefixes continuing with CR/LF/tab (below U+001F, they sort before the separator and
        // so before the whole shorter id) and with ordinary characters (above it). Rendered ids
        // really do contain CR/LF/tab; none contains U+001F itself, and it is exactly that
        // absence that makes the field-wise comparison reproduce the concatenation.
        var ids = new[]
        {
            "", "A", "A\r", "A\n", "A\t", "A0", "AA", "B", "B\nC", "B0",
            "Ns.Space<T>.Run(\u0000int):void", "Ns.Space<T>.Run(\u0000int):void\r", "x", "xy",
            "xy\n", "xy\u001ez", "Ns.X.M(string):void", "Ns.X.M(string\u000b):void",
        };
        var edges = new List<MethodCallEdge>();
        var n = 0;
        foreach (var source in ids)
        {
            foreach (var target in ids)
            {
                foreach (var callType in new[] { CallType.Unknown, CallType.MethodCall, CallType.ConstructorCall })
                {
                    edges.Add(new MethodCallEdge
                    {
                        Id = $"e{++n}",
                        SourceId = source,
                        TargetId = target,
                        CallType = callType,
                        EvidenceKind = n % 2 == 0 ? AnalysisEvidenceKind.SourceRoslynDirect : AnalysisEvidenceKind.SourceUnresolved,
                        CallLocation = new CallLocation(),
                    });
                }
            }
        }

        // Shuffle deterministically so the sort under test actually reorders.
        var rng = new Random(70);
        var shuffled = edges.OrderBy(_ => rng.Next()).ToList();

        var expected = CollapseReference(new CallGraph { Edges = [.. shuffled.Select(CloneEdge)] });
        var actual = new CallGraph { Edges = [.. shuffled.Select(CloneEdge)] };
        ReachabilityAnalyzer.CollapseDuplicateCallSites(actual);

        // Group keys in output order: identical sequences mean identical grouping and ordering.
        Assert.Equal(
            expected.Select(CollapseKeyReference),
            actual.Edges.Select(CollapseKeyReference));
    }

    [Fact]
    public void CompareCollapseEdges_SortsPrefixContinuationsBelowSeparatorBeforeThePrefix()
    {
        MethodCallEdge Edge(string source) => new()
        {
            SourceId = source,
            TargetId = "T",
            CallType = CallType.MethodCall,
            EvidenceKind = AnalysisEvidenceKind.SourceRoslynDirect,
            CallLocation = new CallLocation(),
        };

        // "A\r..." continues below the separator: the concatenated key sorts it BEFORE "A",
        // where a plain field-wise comparison would put it after.
        var edges = new List<MethodCallEdge> { Edge("A"), Edge("A\rX") };
        GraphAssembly.StableSortInPlace(edges, GraphAssembly.CompareCollapseEdges);
        Assert.Equal("A\rX", edges[0].SourceId);
        Assert.Equal("A", edges[1].SourceId);

        // Ordinary continuation ("AA" continues with 'A' > U+001F): the prefix sorts first,
        // as both comparisons agree.
        edges = [Edge("A"), Edge("AA")];
        GraphAssembly.StableSortInPlace(edges, GraphAssembly.CompareCollapseEdges);
        Assert.Equal("A", edges[0].SourceId);
        Assert.Equal("AA", edges[1].SourceId);
    }

    [Fact]
    public void CollapseDuplicateCallSites_CountsAMissingCallLocationApartFromAnEmptyOne()
    {
        // The old key rendered a missing location as "::" and an empty one as ":0:0", so the
        // two are distinct call sites; only the null and empty file name render alike.
        MethodCallEdge Edge(int id, CallLocation? location) => new()
        {
            Id = $"e{id}",
            SourceId = "Ns.A.Run():void",
            TargetId = "Ns.B.Save():void",
            CallType = CallType.MethodCall,
            EvidenceKind = AnalysisEvidenceKind.SourceRoslynDirect,
            CallLocation = location!,
        };

        var graph = new CallGraph
        {
            Edges = [Edge(1, null), Edge(2, new CallLocation()), Edge(3, new CallLocation { FileName = string.Empty })]
        };
        var expected = CollapseReference(new CallGraph { Edges = [.. graph.Edges.Select(CloneEdge)] });

        ReachabilityAnalyzer.CollapseDuplicateCallSites(graph);

        AssertSameCollapse(expected, graph);
        Assert.Equal(2, graph.Edges[0].CallSiteCount);
    }

    [Fact]
    public void CompareCollapseEdges_StaysATotalOrderWhenIdsHoldTheSeparator()
    {
        // The concatenated key was ambiguous for ids holding U+001F itself: ("A\u001fB", "C")
        // and ("A", "B\u001fC") built the same string and collapsed into one edge. Field-wise
        // grouping keeps them apart, and the order is the field-wise order of each field
        // followed by the separator - a consistent total order over every id.
        var ids = new[] { "", "\u001f", "A", "A\u001f", "A\u001fB", "A\u001f\u001f", "A\r", "AB", "B", "B\u001fC", "C" };
        MethodCallEdge Edge(string source, string target, CallType callType) => new()
        {
            SourceId = source,
            TargetId = target,
            CallType = callType,
            EvidenceKind = AnalysisEvidenceKind.SourceRoslynDirect,
            CallLocation = new CallLocation(),
        };

        static int Terminated(MethodCallEdge x, MethodCallEdge y)
        {
            var c = string.CompareOrdinal(x.SourceId + "\u001f", y.SourceId + "\u001f");
            if (c != 0) return c;
            c = string.CompareOrdinal(x.TargetId + "\u001f", y.TargetId + "\u001f");
            if (c != 0) return c;
            c = string.CompareOrdinal(x.CallType + "\u001f", y.CallType + "\u001f");
            return c != 0 ? c : string.CompareOrdinal(x.EvidenceKind.ToString(), y.EvidenceKind.ToString());
        }

        var edges = (from source in ids
                     from target in ids
                     from callType in new[] { CallType.MethodCall, CallType.ConstructorCall }
                     select Edge(source, target, callType)).ToList();
        foreach (var x in edges)
        {
            foreach (var y in edges)
            {
                Assert.Equal(Math.Sign(Terminated(x, y)), Math.Sign(GraphAssembly.CompareCollapseEdges(x, y)));
            }
        }

        var graph = new CallGraph { Edges = [Edge("A\u001fB", "C", CallType.MethodCall), Edge("A", "B\u001fC", CallType.MethodCall)] };
        ReachabilityAnalyzer.CollapseDuplicateCallSites(graph);
        Assert.Equal(["A", "A\u001fB"], graph.Edges.Select(edge => edge.SourceId));
    }

    private static MethodNode ReachabilityNode(string id, MethodIdentity? identity = null, params AnalysisEvidenceKind[] evidence) => new()
    {
        Id = id,
        Name = id,
        ClassName = "C",
        Namespace = "Ns",
        FileName = "/src/C.cs",
        Identity = identity,
        Evidence = [.. evidence.Select(kind => new AnalysisEvidence { Kind = kind })],
    };

    [Fact]
    public void Compute_ToleratesANodeIdentityWithoutAnEvidenceList()
    {
        var graph = new CallGraph { Nodes = [ReachabilityNode("Ns.C.M():void", new MethodIdentity { Evidence = null! })] };

        var (nodes, _, _) = ReachabilityAnalyzer.Compute(graph, [], []);

        Assert.False(Assert.Single(nodes).KeepAlive);
    }

    [Fact]
    public void Compute_IsIndependentOfTheCallersEdgeOrderAndLeavesItAlone()
    {
        // A random graph with duplicate pairs under several call types, self-loops, cycles and
        // keep-alive evidence, collapsed the way the pipeline does it. Compute over the sorted
        // list and over a shuffled copy must agree on every fact, and the shuffled list must
        // come back in the order it went in: the run-length builds sort a copy, never the
        // caller's list.
        var rng = new Random(7070);
        var ids = Enumerable.Range(0, 120).Select(i => $"Ns.T{i % 7}.M{i}():void").ToArray();
        var kinds = new[] { AnalysisEvidenceKind.SourceRoslynDirect, AnalysisEvidenceKind.FrameworkModel, AnalysisEvidenceKind.ReflectionHeuristic };
        var nodes = ids.Select((id, i) => i % 9 == 0
            ? ReachabilityNode(id, new MethodIdentity { Evidence = [AnalysisEvidenceKind.ReflectionHeuristic] }, AnalysisEvidenceKind.FrameworkModel)
            : ReachabilityNode(id)).ToList();
        var edges = new List<MethodCallEdge>();
        for (var i = 0; i < 220; i++)
        {
            var source = ids[rng.Next(ids.Length)];
            var target = rng.Next(12) == 0 ? source : ids[rng.Next(ids.Length)];
            edges.Add(new MethodCallEdge
            {
                Id = $"e{i}",
                SourceId = source,
                TargetId = target,
                CallType = rng.Next(3) == 0 ? CallType.ConstructorCall : CallType.MethodCall,
                EvidenceKind = kinds[rng.Next(kinds.Length)],
                CallLocation = new CallLocation { FileName = "/src/C.cs", LineNumber = rng.Next(1, 50), ColumnNumber = 1 },
                Evidence = rng.Next(4) == 0 ? [new AnalysisEvidence { Kind = kinds[rng.Next(kinds.Length)] }] : [],
            });
        }

        var sortedGraph = new CallGraph { Nodes = nodes, Edges = edges };
        ReachabilityAnalyzer.CollapseDuplicateCallSites(sortedGraph);
        var shuffled = sortedGraph.Edges.OrderBy(_ => rng.Next()).ToList();
        var shuffledOrder = shuffled.Select(edge => edge.Id).ToList();
        var shuffledGraph = new CallGraph { Nodes = nodes, Edges = shuffled };
        EntryPoint Entry(int index) => new() { Id = $"ep{index}", Kind = "Test", MethodId = ids[index] };

        var fromSorted = ReachabilityAnalyzer.Compute(sortedGraph, [Entry(1), Entry(5)], []);
        var fromShuffled = ReachabilityAnalyzer.Compute(shuffledGraph, [Entry(1), Entry(5)], []);

        Assert.Equal(shuffledOrder, shuffledGraph.Edges.Select(edge => edge.Id));
        Assert.Equal(JsonSerializer.Serialize(fromSorted.Nodes), JsonSerializer.Serialize(fromShuffled.Nodes));
        Assert.Equal(JsonSerializer.Serialize(fromSorted.Clusters), JsonSerializer.Serialize(fromShuffled.Clusters));
        Assert.Contains(fromSorted.Clusters, cluster => cluster.Id.StartsWith("scc-self-", StringComparison.Ordinal));
        Assert.Contains(fromSorted.Nodes, fact => fact.KeepAlive);

        // Fan-in/out against the hash-set definition the run-length counts replaced.
        var nonSelf = sortedGraph.Edges.Where(edge => edge.SourceId != edge.TargetId).ToList();
        foreach (var fact in fromSorted.Nodes)
        {
            Assert.Equal(nonSelf.Where(edge => edge.TargetId == fact.NodeId).Select(edge => edge.SourceId).Distinct().Count(), fact.FanIn);
            Assert.Equal(nonSelf.Where(edge => edge.SourceId == fact.NodeId).Select(edge => edge.TargetId).Distinct().Count(), fact.FanOut);
        }
    }

    #endregion
}
