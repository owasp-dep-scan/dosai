using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #120: the reachable-mode dependency walk follows virtual and interface dispatch to
///     the types reached code instantiates (rapid type analysis). Evidence comes from reached
///     bodies only (newobj, box, ldtoken, the generic arguments and signature types of called
///     members), with no class-hierarchy fallback, and the closure fires in both directions so
///     the result does not depend on dequeue order.
/// </summary>
public class Issue120RtaDispatchTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue120-" + Guid.NewGuid().ToString("N"));

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

    private const string LibrarySource = """
using System;

namespace Rta;

public interface ISink
{
    void Write(string value);
}

public class UsedSink : ISink
{
    public void Write(string value) => Console.WriteLine(value);
}

public class ExplicitSink : ISink
{
    void ISink.Write(string value) => Console.WriteLine(value);
}

public class UnusedSink : ISink
{
    public void Write(string value) => Console.WriteLine(value);
}

public static class Factory
{
    public static ISink Create() => new UnusedSink();
}

public static class Helper
{
    public static void Call(ISink sink, string value) => sink.Write(value);
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
            Assert.True(compilation.Emit(stream).Success, assemblyName);
        }

        return dllPath;
    }

    private static string BuildTree(string root, string appSource)
    {
        var output = System.IO.Path.Combine(root, "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        var library = Emit(output, "RtaLib", LibrarySource);
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

    private static DataFlowResult AnalyzeReachable(string path)
    {
        using var scope = DependencyFlowScope.Apply(DependencyFlowMode.Reachable);
        return DataFlowAnalyzer.Analyze(path);
    }

    [Fact]
    public void Reachable_InterfaceDispatch_FollowsOnlyInstantiatedTypes()
    {
        using var fixture = new TempDir();
        BuildTree(fixture.Path, """
using System;

namespace App;

public static class Program
{
    public static void Main(string[] args)
    {
        var used = new Rta.UsedSink();
        var explicitSink = new Rta.ExplicitSink();
        Rta.Helper.Call(used, args[0]);
        Rta.Helper.Call(explicitSink, args[1]);
    }
}
""");

        var result = AnalyzeReachable(fixture.Path);
        var symbols = result.MethodSummaries.Select(summary => summary.Method).ToList();

        // The instantiated implementation is followed through the interface call, the explicit
        // interface implementation (metadata name Rta.ISink.Write) included.
        Assert.Contains(symbols, symbol => symbol.Contains("Rta.UsedSink.Write", StringComparison.Ordinal));
        Assert.Contains(symbols, symbol => symbol.Contains("Rta.ISink.Write", StringComparison.Ordinal));
        // The never-instantiated implementation and the factory that constructs it are not
        // reached: no class-hierarchy fallback, and evidence from unreached bodies does not count.
        Assert.DoesNotContain(symbols, symbol => symbol.Contains("UnusedSink", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, symbol => symbol.Contains("Rta.Factory", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Nodes, node => (node.Symbol ?? string.Empty).Contains("UnusedSink", StringComparison.Ordinal));
    }

    /// <summary>The two-directional firing rule: a callvirt recorded before its type is instantiated still fires.</summary>
    [Fact]
    public void Reachable_DispatchBeforeConstruction_GivesTheSameSlicesAsAfter()
    {
        string[] Slices(DataFlowResult result) => result.Slices
            .Select(slice => slice.Summary ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var constructedFirst = new TempDir();
        BuildTree(constructedFirst.Path, """
namespace App;

public static class Program
{
    private static Rta.UsedSink? sink;

    public static void Main(string[] args)
    {
        sink = new Rta.UsedSink();
        Rta.Helper.Call(sink, args[0]);
    }
}
""");

        using var dispatchedFirst = new TempDir();
        BuildTree(dispatchedFirst.Path, """
namespace App;

public static class Program
{
    private static Rta.UsedSink? sink;

    public static void Main(string[] args)
    {
        Rta.Helper.Call(sink!, args[0]);
        sink = new Rta.UsedSink();
    }
}
""");

        // The callvirt site precedes the newobj in the second app's IL; the walk must still
        // fire the recorded key when the type is admitted, so both give the same slices.
        Assert.Equal(Slices(AnalyzeReachable(constructedFirst.Path)), Slices(AnalyzeReachable(dispatchedFirst.Path)));
        Assert.Contains(AnalyzeReachable(constructedFirst.Path).MethodSummaries, summary => summary.Method.Contains("Rta.UsedSink.Write", StringComparison.Ordinal));
    }
}
