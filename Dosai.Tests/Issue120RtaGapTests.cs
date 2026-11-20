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

    private static string BuildTree(string root, string librarySource, string appSource)
    {
        var output = System.IO.Path.Combine(root, "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        var library = Emit(output, "RtaLib", librarySource);
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

    private static List<string> ReachedMethods(string librarySource, string appSource)
    {
        using var fixture = new TempDir();
        BuildTree(fixture.Path, librarySource, appSource);
        // A hang is a failure, not a stuck test run.
        var task = Task.Run(() =>
        {
            using var scope = DependencyFlowScope.Apply(DependencyFlowMode.Reachable);
            return DataFlowAnalyzer.Analyze(fixture.Path);
        });
        Assert.True(task.Wait(TimeSpan.FromSeconds(120)), "the reachable walk did not finish");
        return task.Result.MethodSummaries.Select(summary => summary.Method).ToList();
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
}
