using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #120 follow-ups: the reachable walk's rapid type analysis must terminate on
///     self-referencing type tables and follow dispatch through generic instantiations,
///     method specifications, application subclasses of dependency types, constrained calls,
///     static abstract members and explicit interface overrides.
/// </summary>
public class Issue120RtaGapTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue120b-" + Guid.NewGuid().ToString("N"));

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
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        return dllPath;
    }

    private static string EmitVisualBasic(string outputDirectory, string assemblyName, string source)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (Microsoft.CodeAnalysis.MetadataReference)entry.Reference).ToList();
        var compilation = Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation.Create(assemblyName,
            [Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree.ParseText(source, path: $"{assemblyName}.vb")],
            metadata,
            new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var dllPath = System.IO.Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var stream = File.Create(dllPath))
        {
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        return dllPath;
    }

    private static string BuildTree(string root, string librarySource, string appSource, bool visualBasic = false)
    {
        var output = System.IO.Path.Combine(root, "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        var library = visualBasic ? EmitVisualBasic(output, "RtaLib", librarySource) : Emit(output, "RtaLib", librarySource);
        Emit(output, "App", appSource, library);
        File.WriteAllText(System.IO.Path.Combine(output, "App.deps.json"), """
{
  "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
  "targets": {
    ".NETCoreApp,Version=v10.0": {
      "App/1.0.0": { "dependencies": { "RtaLib": "1.0.0" }, "runtime": { "App.dll": {} } },
      "RtaLib/1.0.0": { "runtime": { "RtaLib.dll": {} } }
    }
  },
  "libraries": {
    "App/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
    "RtaLib/1.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-x", "path": "rtalib/1.0.0", "hashPath": "" }
  }
}
""");
        return output;
    }

    private static List<string> ReachedMethods(string librarySource, string appSource, bool visualBasic = false)
    {
        using var fixture = new TempDir();
        BuildTree(fixture.Path, librarySource, appSource, visualBasic);
        // A hang is a failure, not a stuck test run.
        var task = Task.Run(() =>
        {
            using var scope = DependencyFlowScope.Apply(DependencyFlowMode.Reachable);
            return DataFlowAnalyzer.Analyze(fixture.Path);
        });
        Assert.True(task.Wait(TimeSpan.FromSeconds(120)), "the reachable walk did not finish");
        var summaries = task.Result.MethodSummaries.Select(summary => summary.Method).ToList();
        return summaries;
    }

    private static bool Reached(List<string> methods, string text) => methods.Any(method => method.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Reachable_TypeNamedItsOwnBase_Terminates()
    {
        // Result<T> : Result is declared first; before the arity-keyed type table both were
        // the open name Result and the base-chain walk looped on it forever.
        var methods = ReachedMethods("""
using System;
namespace R;
public abstract class Result<T> : Result { }
public abstract class Result { public abstract void Show(string v); public static void Show(Result r, string v) => r.Show(v); }
public class Ok : Result<int> { public override void Show(string v) => Console.WriteLine(v); }
public class Unused : Result<long> { public override void Show(string v) => Console.WriteLine(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => R.Result.Show(new R.Ok(), a[0]); }
""");
        Assert.True(Reached(methods, "R.Ok.Show"));
        Assert.False(Reached(methods, "R.Unused"));
    }

    [Fact]
    public void Reachable_GenericInterfaceAndGenericBase_AreInTheClosure()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface IHandler<T> { void Handle(T value); }
public class Exec : IHandler<string> { public void Handle(string value) => Console.WriteLine(value); }
public class Never : IHandler<string> { public void Handle(string value) => Console.WriteLine(value); }
public class Box<T> { public virtual void Open(string v) => Console.WriteLine(v); }
public class Impl : Box<int> { public override void Open(string v) => Console.WriteLine(v); }
public class Idle : Box<int> { public override void Open(string v) => Console.WriteLine(v); }
public static class H
{
    public static void Run(IHandler<string> h, string s) => h.Handle(s);
    public static void Open(Box<int> b, string v) => b.Open(v);
}
""", """
namespace App;
public static class Program { public static void Main(string[] a) { Rta.H.Run(new Rta.Exec(), a[0]); Rta.H.Open(new Rta.Impl(), a[1]); } }
""");
        Assert.True(Reached(methods, "Rta.Exec.Handle"));
        Assert.True(Reached(methods, "Rta.Impl.Open"));
        Assert.False(Reached(methods, "Rta.Never"));
        Assert.False(Reached(methods, "Rta.Idle"));
    }

    [Fact]
    public void Reachable_NewobjOfGenericType_AdmitsTheGenericDefinition()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface ISink { void Write(string value); }
public class GenSink<T> : ISink { public void Write(string value) => Console.WriteLine(value); }
public class Other<T> : ISink { public void Write(string value) => Console.WriteLine(value); }
public static class Helper { public static void Call(ISink sink, string value) => sink.Write(value); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.Helper.Call(new Rta.GenSink<int>(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.GenSink"));
        Assert.False(Reached(methods, "Rta.Other"));
    }

    [Fact]
    public void Reachable_BoxOfGenericStruct_AdmitsTheStruct()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface ISink { void Write(string value); }
public struct GenStruct<T> : ISink { public void Write(string value) => Console.WriteLine(value); }
public static class Helper { public static void Call(ISink sink, string value) => sink.Write(value); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) { ISink s = new Rta.GenStruct<int>(); Rta.Helper.Call(s, a[0]); } }
""".Replace("ISink", "Rta.ISink"));
        Assert.True(Reached(methods, "Rta.GenStruct"));
    }

    [Fact]
    public void Reachable_MethodInstantiationArgument_IsEvidence()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface ISink { void Write(string value); }
public class FileSink : ISink { public void Write(string value) => Console.WriteLine(value); }
public class NetSink : ISink { public void Write(string value) => Console.WriteLine(value); }
public static class Registry { public static T Make<T>() where T : ISink, new() => new T(); }
public static class Helper { public static void Call(ISink sink, string value) => sink.Write(value); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.Helper.Call(Rta.Registry.Make<Rta.FileSink>(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.FileSink.Write"));
        Assert.False(Reached(methods, "Rta.NetSink"));
    }

    [Fact]
    public void Reachable_ApplicationTypeDerivingFromDependencyType_ReachesTheDependencyBase()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public abstract class Top { public abstract void Run(string v); }
public class Mid : Top { public override void Run(string v) => Console.WriteLine(v); }
public class Spare : Top { public override void Run(string v) => Console.WriteLine(v); }
public static class Helper { public static void Call(Top top, string v) => top.Run(v); }
""", """
namespace App;
public class Mine : Rta.Mid { }
public static class Program { public static void Main(string[] a) => Rta.Helper.Call(new Mine(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.Mid.Run"));
        Assert.False(Reached(methods, "Rta.Spare"));
    }

    [Fact]
    public void Reachable_ConstrainedCallOnAStruct_ReachesTheImplementation()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface IRun { void Run(string v); }
public struct S : IRun { public void Run(string v) => Console.WriteLine(v); }
public struct Idle : IRun { public void Run(string v) => Console.WriteLine(v); }
public static class G { public static void Go<T>(T item, string v) where T : IRun => item.Run(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.G.Go(new Rta.S(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.S.Run"));
        Assert.False(Reached(methods, "Rta.Idle"));
    }

    [Fact]
    public void Reachable_StaticAbstractMember_ReachesTheImplementation()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface IMake { static abstract void Make(string v); }
public class A : IMake { public static void Make(string v) => Console.WriteLine(v); }
public class B : IMake { public static void Make(string v) => Console.WriteLine(v); }
public static class G { public static void Go<T>(string v) where T : IMake => T.Make(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.G.Go<Rta.A>(a[0]); }
""");
        Assert.True(Reached(methods, "Rta.A.Make"));
        Assert.False(Reached(methods, "Rta.B"));
    }

    [Fact]
    public void Reachable_DefaultMethodOverriddenInDerivedInterface_IsReached()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface I { void M(string v) => Console.WriteLine(v); }
public interface J : I { void I.M(string v) => Console.WriteLine(v); }
public class C : J { }
public static class Helper { public static void Call(I i, string v) => i.M(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.Helper.Call(new Rta.C(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.J.Rta.I.M"));
    }

    [Fact]
    public void Reachable_SealedNonGenericBox_DoesNotHideTheGenericDispatch()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public sealed class Box { public void Open(string v) => Console.WriteLine(v); }
public abstract class Box<T> { public abstract void Open(string v); }
public class IntBox : Box<int> { public override void Open(string v) => Console.WriteLine(v); }
public static class Helper { public static void Call(Box<int> b, string v) => b.Open(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.Helper.Call(new Rta.IntBox(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.IntBox.Open"));
    }

    [Fact]
    public void Reachable_NestedDeclaringType_KeepsItsQualifiedDispatchKey()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public class Outer { public interface IJob { void Do(string v); } }
public class Other { public interface IJob { void Do(string v); } }
public class Real : Outer.IJob { public void Do(string v) => Console.WriteLine(v); }
public class Fake : Other.IJob { public void Do(string v) => Console.WriteLine(v); }
public static class Helper { public static void Call(Outer.IJob j, string v) => j.Do(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) { Rta.Helper.Call(new Rta.Real(), a[0]); var f = new Rta.Fake(); } }
""");
        Assert.True(Reached(methods, "Rta.Real.Do"));
        Assert.False(Reached(methods, "Rta.Fake.Do"));
    }

    [Fact]
    public void Reachable_AnOverloadInADerivedClass_DoesNotHideTheBaseOverride()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public abstract class Shape { public abstract void Draw(string v); }
public class CircleBase : Shape { public override void Draw(string v) => Console.WriteLine(v); }
public class Circle : CircleBase { public void Draw(int n) => Console.WriteLine(n); }
public static class Helper { public static void Call(Shape s, string v) => s.Draw(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.Helper.Call(new Rta.Circle(), a[0]); }
""");
        Assert.True(Reached(methods, "Rta.CircleBase.Draw"));
    }

    [Fact]
    public void Reachable_AGenericTypeAndAnUnrelatedNonGenericOneOfTheSameName_KeepTheirOwnBodies()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface IRun { void Run(string v); }
public class Circle { public void Run(string v) => Console.WriteLine(v); }
public class Circle<T> : IRun { public void Run(string v) => Console.WriteLine(v); }
public static class Helper { public static void Call(IRun r, string v) => r.Run(v); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) { Rta.Helper.Call(new Rta.Circle<int>(), a[0]); } }
""");
        Assert.True(Reached(methods, "Rta.Circle`1.Run"));
        Assert.False(Reached(methods, "Rta.Circle.Run"));
    }

    [Fact]
    public void Reachable_StaticAbstractMemberOfAFrameworkInterface_ReachesTheImplementation()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public readonly struct Money : IParsable<Money>
{
    public static Money Parse(string s, IFormatProvider? provider) { Console.WriteLine(s); return default; }
    public static bool TryParse(string? s, IFormatProvider? provider, out Money result) { Console.WriteLine(s); result = default; return true; }
}
public static class G { public static T P<T>(string s) where T : IParsable<T> => T.Parse(s, null); }
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.G.P<Rta.Money>(a[0]); }
""");
        Assert.True(Reached(methods, "Rta.Money.Parse"));
    }

    [Fact]
    public void Reachable_NestedAndArrayParameterTypes_AreInstantiationEvidence()
    {
        var methods = ReachedMethods("""
using System;
namespace Rta;
public interface IRun { void Run(string v); }
public class Outer { public class Impl : IRun { public void Run(string v) => Console.WriteLine(v); } }
public class Flat { public class Item : IRun { public void Run(string v) => Console.WriteLine(v); } }
public class Idle : IRun { public void Run(string v) => Console.WriteLine(v); }
public static class Consumer
{
    public static void Take(Outer.Impl impl, string v) { IRun r = impl; r.Run(v); }
    public static void TakeAll(Flat.Item[] items, string v) { IRun r = items[0]; r.Run(v); }
}
""", """
namespace App;
public static class Program { public static void Main(string[] a) { Rta.Consumer.Take(null!, a[0]); Rta.Consumer.TakeAll(null!, a[0]); } }
""");
        Assert.True(Reached(methods, "Impl.Run"));
        Assert.True(Reached(methods, "Item.Run"));
        Assert.False(Reached(methods, "Rta.Idle"));
    }

    [Fact]
    public void Reachable_MethodImplRow_NamesTheImplementationOfADifferentlyNamedMethod()
    {
        // Visual Basic names the method freely (Emit) and records the interface member in a
        // MethodImpl row, so neither the name nor the dotted form matches.
        var methods = ReachedMethods("""
Option Strict On
Imports System

Namespace Rta
    Public Interface ISink
        Sub Write(value As String)
    End Interface

    Public Class VbSink
        Implements ISink
        Public Sub Emit(value As String) Implements ISink.Write
            Console.WriteLine(value)
        End Sub
    End Class

    Public Class VbIdle
        Implements ISink
        Public Sub Emit2(value As String) Implements ISink.Write
            Console.WriteLine(value)
        End Sub
    End Class

    Public Module Helper
        Public Sub Invoke(sink As ISink, value As String)
            sink.Write(value)
        End Sub
    End Module
End Namespace
""", """
namespace App;
public static class Program { public static void Main(string[] a) => Rta.Helper.Invoke(new Rta.VbSink(), a[0]); }
""", visualBasic: true);
        Assert.True(Reached(methods, "Rta.VbSink.Emit"));
        Assert.False(Reached(methods, "Rta.VbIdle"));
    }
}
