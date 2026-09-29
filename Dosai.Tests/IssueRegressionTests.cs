using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Regression tests written against the reported reproductions (locale tests for #63 live in
// LocaleInvarianceTests.cs):
// - #64 the Roslyn InvalidOperationException on a generic method over a generic interface
// - #65 symbol analysis on one core and super-linear in the file count (parallel workers,
//   bucketed dispatch, memoized lookups, byte-identical output to the sequential path)
// Partial class, same xunit collection as the rest of DosaiTests: these tests flip the thread
// culture and the static worker knob, which is only safe serialized against the console-
// redirecting tests.
public partial class DosaiTests
{
    private static readonly JsonSerializerOptions JsonStringEnums = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    #region Issue #64 - Roslyn failure on generic method over a generic interface

    [Fact]
    public void GetMethods_GenericMethodOnGenericInterface_DoesNotAbortTheScan()
    {
        // The 21-line reproduction from the issue: before the guard, Roslyn threw
        // InvalidOperationException out of FindImplementationForInterfaceMember and `methods`
        // exited 1 with no output.
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
""");

        var slice = ReadMethods(tempDirectory.Path);

        // The direct interface call survives; only the synthesized dispatch candidate for the
        // implementing class is abandoned.
        Assert.Contains(slice.MethodCalls!, call =>
            call.SourceId?.Contains("Builder.Join", StringComparison.Ordinal) == true &&
            call.TargetId?.Contains("Join", StringComparison.Ordinal) == true);
        // The abandonment is visible without --debug, like the other containment diagnostics.
        Assert.Contains(slice.Diagnostics!, diagnostic =>
            diagnostic.StartsWith("Dispatch resolution failed for 1 call site(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMethods_StructImplementingGenericInterfaceMethod_DoesNotAbortTheScan()
    {
        // Second shape from the issue thread (dotnet/runtime JIT test trees): a struct
        // implementing a generic method declared on a generic interface.
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
        Assert.Contains(slice.Methods!, method => method.Name == "Foo");
        Assert.Contains(slice.Diagnostics!, diagnostic =>
            diagnostic.StartsWith("Dispatch resolution failed for 1 call site(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void GetMethods_GenericInterfaceDispatch_StillProducesCandidateEdges()
    {
        // The guard must not suppress healthy generic dispatch: a generic method on a
        // non-generic interface still synthesizes its candidate edge. (A generic method on a
        // GENERIC interface is itself the shape that trips the Roslyn failure - see the two
        // repro tests above - so the healthy control uses a non-generic interface.)
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "HealthyGenericDispatch.cs"), """
public interface IGreeter
{
    string Greet<U>(string value);
}

public class Greeter : IGreeter
{
    public string Greet<U>(string value) => value;
}

public static class GreeterEntry
{
    public static string Run()
    {
        IGreeter greeter = new Greeter();
        return greeter.Greet<int>("hello");
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        Assert.Contains(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate } &&
            call.TargetId?.Contains("Greeter.Greet", StringComparison.Ordinal) == true);
        // And nothing was abandoned on this healthy shape.
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic =>
            diagnostic.StartsWith("Dispatch resolution failed", StringComparison.Ordinal));
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
    public interface IShape{{index}}
    {
        int Area<U>(U scale);
    }

    public class Circle{{index}} : IShape{{index}}
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
            IShape{{index}} shape = new Circle{{index}}(int.Parse(args[0]));
            var area = shape.Area<string>("2");
            var sealedShape = new SealedCircle{{index}}(3);
            var text = sealedShape.Describe();
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

        var previous = Depscan.Dosai.MaxSymbolAnalysisWorkers;
        string sequential;
        string parallel;
        try
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = 1;
            sequential = Depscan.Dosai.GetMethods(tempDirectory.Path);
            Depscan.Dosai.MaxSymbolAnalysisWorkers = 6;
            parallel = Depscan.Dosai.GetMethods(tempDirectory.Path);
        }
        finally
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = previous;
        }

        // The parallel run must be byte-identical to the sequential one: per-file results merge
        // in file order, so the worker count can never reorder methods, calls, or edges.
        Assert.Equal(NormalizeGeneratedAt(sequential), NormalizeGeneratedAt(parallel));

        // The tree itself must exercise the interesting paths, so the equality above is not
        // vacuous: interface dispatch candidates, exact sealed resolution, and VB members.
        var slice = JsonSerializer.Deserialize<MethodsSlice>(parallel, JsonStringEnums);
        Assert.NotNull(slice);
        Assert.True(slice.Methods!.Count >= 60, $"expected a substantive inventory, got {slice.Methods.Count}");
        Assert.Contains(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate } &&
            call.TargetId?.Contains("Circle", StringComparison.Ordinal) == true);
        Assert.Contains(slice.Methods!, method => method.Name == "Twice");
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

        var previous = Depscan.Dosai.MaxSymbolAnalysisWorkers;
        var recorder = new LineRecorder();
        try
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = 3;
            lock (ConsoleOutputLock)
            {
                var originalError = Console.Error;
                Console.SetError(recorder);
                try
                {
                    DebugLog.Configure(true);
                    Depscan.Dosai.GetMethods(tempDirectory.Path);
                }
                finally
                {
                    DebugLog.Configure(false);
                    Console.SetError(originalError);
                }
            }
        }
        finally
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = previous;
        }

        var lines = recorder.Snapshot();
        Assert.Contains(lines, line => line.Contains("symbol analysis workers: 3", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("start methods.dispatch-index", StringComparison.Ordinal));
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
        var rta = Assert.Single(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate, DispatchConfidence: "rta-candidate" } &&
            call.TargetId?.Contains("HttpTransport.Send", StringComparison.Ordinal) == true);
        var cha = Assert.Single(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate, DispatchConfidence: "cha-candidate" } &&
            call.TargetId?.Contains("QueueTransport.Send", StringComparison.Ordinal) == true);
        Assert.True(
            slice.MethodCalls!.IndexOf(rta) < slice.MethodCalls.IndexOf(cha),
            "the instantiated implementer must rank before the uninstantiated one");
    }

    [Fact]
    public void GetMethods_VirtualCallThroughBase_ResolvesOverrideCandidate()
    {
        // A base-call inside a derived type must resolve to the override through the base-type
        // bucket: the candidate edge points at SealedShape.Describe, ranked rta-candidate
        // because the program instantiates SealedShape. This pins the bucketed lookup's
        // semantics for class-hierarchy dispatch (the previous full scan found the same edge).
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Sealed.cs"), """
public class ShapeBase
{
    public virtual string Describe() => "base";
}

public sealed class SealedShape : ShapeBase
{
    public override string Describe() => "sealed";

    public string DescribeViaBase() => base.Describe();
}

public static class SealedEntry
{
    public static void Main()
    {
        var shape = new SealedShape();
        shape.DescribeViaBase();
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);
        Assert.Contains(slice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate, DispatchConfidence: "rta-candidate" } &&
            call.TargetId?.Contains("SealedShape.Describe", StringComparison.Ordinal) == true);
    }

    #endregion
}
