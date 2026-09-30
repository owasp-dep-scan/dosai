using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Anonymous and local functions in the call graph: each is its own node, named after the member
// that declares it (Dosai.NestedFunctionName). A lambda's symbol has an empty name, so its plain
// signature was `Ns.Type.(params):ret` and every same-shaped lambda of a type collapsed onto one
// node; a local function's signature was that of a same-named member.
// Partial class, same xunit collection as the rest of DosaiTests: the worker-count test flips
// the static worker knob.
public partial class DosaiTests
{
    private const string NestedFunctionShapes = """
using System;
using System.Collections.Generic;
using System.Linq;
namespace App;

public partial class Shapes
{
    private readonly Func<int> field = () => Sink.A();
    public Func<int> Auto { get; } = () => Sink.B();
    public Func<int> Arrow => () => Sink.C();

    public Shapes() { Run(() => Sink.E()); }

    public void Run(Action action) => action();
    public void Run(int value) { Run(() => Sink.F()); Run(() => Sink.G()); }
    public void Run(string value) { Run(() => { Run(() => Sink.H()); }); }

    public int Other(List<int> items) => items.Count(item => item > Sink.I()) + Helper();
    public void Anonymous() { Run(delegate { Sink.K(); }); }

    int Helper() => 1;
    public int Locals(int value)
    {
        return Helper();
        int Helper() => Sink.J();
    }
    public int Locals(string value)
    {
        return Helper();
        int Helper() => Sink.M();
    }
}

public static class Sink
{
    public static int A() => 0; public static int B() => 0; public static int C() => 0; public static int E() => 0;
    public static int F() => 0; public static int G() => 0; public static int H() => 0; public static int I() => 0;
    public static int J() => 0; public static void K() { } public static int L() => 0; public static int M() => 0;
}
""";

    // The second partial part's path sorts after Shapes.cs (ordinal), so its lambdas number after the first part's.
    private const string NestedFunctionShapesPart = """
using System;
namespace App;

public partial class Shapes
{
    public void Run(double value) { Run(() => Sink.L()); }
}
""";

    private static bool HasEdge(MethodsSlice slice, string source, string target, CallType callType) =>
        slice.CallGraph!.Edges.Any(edge => edge.SourceId == source && edge.TargetId == target && edge.CallType == callType);

    [Fact]
    public void GetMethods_Lambdas_AreDistinctNodesNamedAfterTheirMember()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Shapes.cs"), NestedFunctionShapes);
        File.WriteAllText(Path.Combine(tempDirectory.Path, "ShapesPart.cs"), NestedFunctionShapesPart);

        var slice = ReadMethods(tempDirectory.Path);

        // Same-shaped lambdas across Run's overloads and partial parts, numbered in source order;
        // a nested lambda follows the one that contains it.
        Assert.True(HasEdge(slice, "App.Shapes.Run(int):void", "App.Shapes.<Run>lambda1():void", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Shapes.<Run>lambda1():void", "App.Sink.F():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<Run>lambda2():void", "App.Sink.G():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.Run(string):void", "App.Shapes.<Run>lambda3():void", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Shapes.<Run>lambda3():void", "App.Shapes.<Run>lambda4():void", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Shapes.<Run>lambda4():void", "App.Sink.H():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.Run(double):void", "App.Shapes.<Run>lambda5():void", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Shapes.<Run>lambda5():void", "App.Sink.L():int", CallType.MethodCall));
        // Constructors, field and auto-property initializers, getters, anonymous methods.
        Assert.True(HasEdge(slice, "App.Shapes.<.ctor>lambda1():void", "App.Sink.E():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<field>lambda1():int", "App.Sink.A():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<Auto>lambda1():int", "App.Sink.B():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<get_Arrow>lambda1():int", "App.Sink.C():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<Other>lambda1(int):bool", "App.Sink.I():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<Anonymous>lambda1():void", "App.Sink.K():void", CallType.MethodCall));

        // No node is left without a member name or on the position fallback, and a lambda node
        // shows its name.
        Assert.DoesNotContain(slice.CallGraph!.Nodes, node => node.Id.Contains(".(", StringComparison.Ordinal) || node.Id.Contains('@', StringComparison.Ordinal));
        Assert.Equal("<Run>lambda4", Assert.Single(slice.CallGraph.Nodes, node => node.Id == "App.Shapes.<Run>lambda4():void").Name);
        Assert.Contains(slice.MethodCalls!, call => call is { SourceId: "App.Shapes.<Run>lambda4():void", CallerMethod: "<Run>lambda4", CalledMethod: not null });
        Assert.Contains(slice.MethodCalls!, call => call is { TargetId: "App.Shapes.<Run>lambda4():void", CalledMethod: "<Run>lambda4" });
        // Compiler-generated like the IL's <Run>b__0_0: never reported as dead code on their own.
        Assert.DoesNotContain(slice.DeadCode!, entry => entry.NodeId.Contains("lambda", StringComparison.Ordinal) || entry.NodeId.Contains(".(", StringComparison.Ordinal));
        Assert.Contains(slice.DeadCode!, entry => entry.NodeId == "App.Shapes.Run(string):void");
    }

    // A local function is its own node, apart from a same-named member of the type and from a
    // same-named local function in an overload.
    [Fact]
    public void GetMethods_LocalFunctions_AreNamedAfterTheirMember()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Shapes.cs"), NestedFunctionShapes);

        var slice = ReadMethods(tempDirectory.Path);

        Assert.True(HasEdge(slice, "App.Shapes.Other(System.Collections.Generic.List<int>):int", "App.Shapes.Helper():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.Locals(int):int", "App.Shapes.<Locals>Helper():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<Locals>Helper():int", "App.Sink.J():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.Locals(string):int", "App.Shapes.<Locals>Helper|2():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Shapes.<Locals>Helper|2():int", "App.Sink.M():int", CallType.MethodCall));
        Assert.DoesNotContain(slice.CallGraph!.Edges, edge => edge.SourceId == "App.Shapes.Helper():int");
    }

    // Top-level statements declare their lambdas and local functions in `<Main>$`, as the
    // compiler names them (`<<Main>$>b__0_0`).
    [Fact]
    public void GetMethods_TopLevelLambdasAndLocalFunctions_AreNamedAfterMain()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), """
using System;
Func<int> first = () => Tools.One();
Console.WriteLine(first() + Local());
int Local() => Tools.Two();
static class Tools { public static int One() => 1; public static int Two() => 2; }
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.True(HasEdge(slice, "Program.<Main>$(string[]):void", "Program.<<Main>$>lambda1():int", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "Program.<<Main>$>lambda1():int", "Tools.One():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "Program.<Main>$(string[]):void", "Program.<<Main>$>Local():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "Program.<<Main>$>Local():int", "Tools.Two():int", CallType.MethodCall));
    }

    // Query expressions compile to implicit lambdas per clause, which C# declares by the clause's
    // expression and Visual Basic not at all: they are named after their query, one per query
    // expression, and a clause whose body is a nested query still belongs to the outer one.
    [Fact]
    public void GetMethods_QueryExpressionLambdas_AreNamedAfterTheirQuery()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Q.cs"), """
using System.Linq;
namespace App;
public class Q
{
    int[] xs = new int[0];
    public object M() => from a in xs
                         where Sink.W(a) && xs.Any(y => y > a)
                         select from c in xs select Sink.S(c);
    public object N() => from a in xs select Sink.S(a);
}
public static class Sink { public static bool W(int x) => true; public static int S(int x) => x; }
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "V.vb"), """
Imports System.Linq
Public Class V
    Private xs As Integer() = New Integer() {}
    Public Function M() As Object
        Return From a In xs Where Helpers.W(a) Select Helpers.S(a)
    End Function
End Class
Public Module Helpers
    Public Function W(x As Integer) As Boolean
        Return True
    End Function
    Public Function S(x As Integer) As Integer
        Return x
    End Function
End Module
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.True(HasEdge(slice, "App.Q.M():object", "App.Q.<M>query1(int):bool", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Q.<M>query1(int):bool", "App.Sink.W(int):bool", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Q.<M>query1(int):bool", "App.Q.<M>lambda1(int):bool", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Q.M():object", "App.Q.<M>query1(int):System.Collections.Generic.IEnumerable<int>", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Q.<M>query2(int):int", "App.Sink.S(int):int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Q.<N>query1(int):int", "App.Sink.S(int):int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "V.<M>query1(Integer):Boolean", "Helpers.W(Integer):Boolean", CallType.MethodCall));
        Assert.True(HasEdge(slice, "V.<M>query1(Integer):Integer", "Helpers.S(Integer):Integer", CallType.MethodCall));
        Assert.DoesNotContain(slice.CallGraph!.Nodes, node => node.Id.Contains('@', StringComparison.Ordinal));
    }

    // Visual Basic declares a member by its header statement and a field by its name; the
    // lambdas inside still get their member's name.
    [Fact]
    public void GetMethods_VisualBasicLambdas_AreNamedAfterTheirMember()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Greeter.vb"), """
Imports System
Public Class Greeter
    Private ReadOnly _f As Func(Of Integer) = Function() Helpers.One()
    Public Sub Run()
        Dim a As Func(Of Integer) = Function() Helpers.One()
        Dim b As Action = Sub()
                              Helpers.Two()
                          End Sub
        a()
        b()
    End Sub
    Public Sub Run(x As Integer)
        Dim c As Func(Of Integer) = Function() Helpers.One()
        c()
    End Sub
End Class
Public Module Helpers
    Public Function One() As Integer
        Return 1
    End Function
    Public Sub Two()
    End Sub
End Module
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.True(HasEdge(slice, "Greeter.Run():Void", "Greeter.<Run>lambda1():Integer", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "Greeter.<Run>lambda2():Void", "Helpers.Two():Void", CallType.MethodCall));
        Assert.True(HasEdge(slice, "Greeter.Run(Integer):Void", "Greeter.<Run>lambda3():Integer", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "Greeter.<_f>lambda1():Integer", "Helpers.One():Integer", CallType.MethodCall));
    }

    // A primary constructor's base-type arguments are its constructor initializer: the base
    // constructor call and everything in its arguments belong to the constructor.
    [Fact]
    public void GetMethods_PrimaryConstructorBaseArguments_AreConstructorCalls()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Derived.cs"), """
using System;
namespace App;
public class Derived() : Base(Sink.M(), () => Sink.N());
public class Base(int value, Func<int> factory);
public record Rec(int X) : BaseRec(X);
public record BaseRec(int X);
public static class Sink { public static int M() => 0; public static int N() => 0; }
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.True(HasEdge(slice, "App.Derived..ctor()", "App.Base..ctor(int,System.Func<int>)", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Derived..ctor()", "App.Sink.M():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Derived..ctor()", "App.Derived.<.ctor>lambda1():int", CallType.DelegateInvoke));
        Assert.True(HasEdge(slice, "App.Derived.<.ctor>lambda1():int", "App.Sink.N():int", CallType.MethodCall));
        Assert.True(HasEdge(slice, "App.Rec..ctor(int)", "App.BaseRec..ctor(int)", CallType.MethodCall));
    }

    // Names come from syntax, so every worker count renders the same graph.
    [Fact]
    public void GetMethods_NestedFunctionNames_AreTheSameForEveryWorkerCount()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Shapes.cs"), NestedFunctionShapes);
        File.WriteAllText(Path.Combine(tempDirectory.Path, "ShapesPart.cs"), NestedFunctionShapesPart);

        string Graph(int workers) => WithSymbolAnalysisWorkers(workers, () => JsonSerializer.Serialize(ReadMethods(tempDirectory.Path).CallGraph, JsonStringEnums));

        Assert.Equal(Graph(1), Graph(8));
    }
}
