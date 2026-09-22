using Depscan;
using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Xunit;

namespace Dosai.Tests;

public class DosaiTests
{
    private static readonly object ConsoleOutputLock = new();

    #region GetMethods
    [Fact]
    public void GetMethods_CSharpAssembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestDataCSharpDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(36, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataCSharpDLL);
    }

    // Regression guard for the per-assembly memory blow-up. The fix streams the JSON for the `methods`
    // command straight to the output file (Dosai.WriteMethods) instead of materialising it as a single
    // contiguous string (which is what drove peak RSS into the multi-GB range and eventually overflowed the
    // string allocator). This test fails against the old implementation because (a) WriteMethods did not
    // exist and (b) it asserts that several assemblies inspected together in one run all contribute their
    // members and that the streamed file matches the in-memory string path property for property.
    [Fact]
    public void WriteMethods_MultipleAssembliesInOneRun_StreamsJsonEquivalentToGetMethods()
    {
        using var tempDirectory = new TemporaryDirectory();
        var firstOutput = BuildTemporaryProject(tempDirectory.Path, "MultiAssemblyOne", """
namespace MultiOne
{
    public static class Helper
    {
        public static int Compute(int a, int b) => a + b;
        public static string Greet(string name) => "hello " + name;
    }
    public static class Program { public static void Main() { } }
}
""");
        var secondOutput = BuildTemporaryProject(tempDirectory.Path, "MultiAssemblyTwo", """
namespace MultiTwo
{
    public static class Gateway
    {
        public static string Echo(string value) => value;
    }
    public static class Program { public static void Main() { } }
}
""");
        var thirdOutput = BuildTemporaryProject(tempDirectory.Path, "MultiAssemblyThree", """
namespace MultiThree
{
    public static class Calculator
    {
        public static int Square(int x) => x * x;
    }
    public static class Program { public static void Main() { } }
}
""");

        var combinedDirectory = Path.Combine(tempDirectory.Path, "combined");
        Directory.CreateDirectory(combinedDirectory);
        foreach (var outputDirectory in new[] { firstOutput, secondOutput, thirdOutput })
        {
            var assembly = Directory.EnumerateFiles(outputDirectory, "MultiAssembly*.dll")
                .Single(file => Path.GetFileName(file).StartsWith("MultiAssembly", StringComparison.Ordinal));
            File.Copy(assembly, Path.Combine(combinedDirectory, Path.GetFileName(assembly)));
            var pdb = Path.ChangeExtension(assembly, ".pdb");
            if (File.Exists(pdb))
            {
                File.Copy(pdb, Path.Combine(combinedDirectory, Path.GetFileName(pdb)));
            }
        }

        var outputFile = Path.Combine(tempDirectory.Path, "streamed.json");
        var streamedSlice = Depscan.Dosai.WriteMethods(combinedDirectory, outputFile);

        Assert.True(File.Exists(outputFile));

        // Every assembly inspected in the single run must contribute its members.
        Assert.NotNull(streamedSlice.Methods);
        Assert.Contains(streamedSlice.Methods!, method => method.FileName == "MultiAssemblyOne.dll" && method.ClassName == "Helper");
        Assert.Contains(streamedSlice.Methods!, method => method.FileName == "MultiAssemblyTwo.dll" && method.ClassName == "Gateway");
        Assert.Contains(streamedSlice.Methods!, method => method.FileName == "MultiAssemblyThree.dll" && method.ClassName == "Calculator");
        Assert.Contains(streamedSlice.Methods!, method => method.Name == "Compute" && method.FileName == "MultiAssemblyOne.dll");
        Assert.Contains(streamedSlice.Methods!, method => method.Name == "Echo" && method.FileName == "MultiAssemblyTwo.dll");
        Assert.Contains(streamedSlice.Methods!, method => method.Name == "Square" && method.FileName == "MultiAssemblyThree.dll");

        // The streamed file must match the string-based GetMethods output verbatim on every property except
        // Metadata, which carries a per-run timestamp.
        var streamed = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(outputFile));
        var inMemory = JsonSerializer.Deserialize<JsonElement>(Depscan.Dosai.GetMethods(combinedDirectory));
        Assert.Equal(
            inMemory.EnumerateObject().Select(property => property.Name).ToList(),
            streamed.EnumerateObject().Select(property => property.Name).ToList());
        foreach (var property in inMemory.EnumerateObject().Where(property => property.Name != "Metadata"))
        {
            Assert.Equal(property.Value.GetRawText(), streamed.GetProperty(property.Name).GetRawText());
        }
    }

    [Fact]
    public void GetMethods_AssemblyOnly_IlCallGraphIncludesMethodBodyEdges()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyCallGraphFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Launch(args[0]);
    }

    private static void Launch(string command)
    {
        Process.Start(command);
    }
}
""");

        var result = Depscan.Dosai.GetMethods(Path.Combine(outputDirectory, "AssemblyCallGraphFlow.dll"));
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice?.CallGraph);
        Assert.NotNull(methodsSlice.MethodCalls);
        var callGraph = methodsSlice.CallGraph;
        Assert.NotEmpty(callGraph.Edges);
        Assert.Contains(callGraph.Nodes, node => node.Identity?.Evidence.Contains(AnalysisEvidenceKind.AssemblyIlDirect) == true || node.Identity?.Evidence.Contains(AnalysisEvidenceKind.AssemblyReflection) == true);
        Assert.Contains(callGraph.Edges, edge => edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect && edge.Evidence.Any(evidence => evidence.Kind == AnalysisEvidenceKind.AssemblyIlDirect));
        Assert.Contains(callGraph.Edges, edge => edge.SourceId.Contains("Program.Main", StringComparison.Ordinal) && edge.TargetId.Contains("Program.Launch", StringComparison.Ordinal));
        Assert.Contains(callGraph.Edges, edge => edge.SourceId.Contains("Program.Launch", StringComparison.Ordinal) && edge.TargetId.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal));
        Assert.Contains(callGraph.Nodes, node =>
            node.Id.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal) &&
            node is { IsExternal: true, Module: "System.Diagnostics.Process.dll", FileName: "System.Diagnostics.Process.dll" });
        Assert.Contains(methodsSlice.MethodCalls!, call =>
            call.TargetId is not null &&
            call.TargetId.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal) &&
            call.Module == "System.Diagnostics.Process.dll");
        var nodeIds = callGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(callGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
            Assert.True(edge.CallLocation.LineNumber > 0);
        });
        Assert.Contains(methodsSlice.MethodCalls!, call => call.SourceId is not null && call.SourceId.Contains("Program.Launch", StringComparison.Ordinal) && call.TargetId is not null && call.TargetId.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal) && call.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect);
    }

    [Fact]
    public void GetMethods_AssemblyOnly_MetadataOnlyNodesUseReflectionEvidence()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyMetadataEvidence", """
public interface IPlugin
{
    void Run();
}

public abstract class BasePlugin
{
    public abstract void Execute();
}

public static class Program
{
    public static void Main() { }
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "AssemblyMetadataEvidence.dll"));
        var interfaceNode = Assert.Single(methodsSlice.CallGraph!.Nodes, node =>
            node.Id.Contains("IPlugin.Run", StringComparison.Ordinal));

        Assert.Contains(AnalysisEvidenceKind.AssemblyReflection, interfaceNode.Identity!.Evidence);
        Assert.DoesNotContain(AnalysisEvidenceKind.AssemblyIlDirect, interfaceNode.Identity.Evidence);
        Assert.Contains(interfaceNode.Evidence, evidence =>
            evidence is { Kind: AnalysisEvidenceKind.AssemblyReflection, Source: "assembly-metadata" });
        Assert.DoesNotContain(interfaceNode.Evidence, evidence => evidence.Source == "assembly-il");
    }

    [Fact]
    public void GetMethods_AssemblyOnlyAsyncStateMachine_CollapsesMoveNextToUserMethod()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyAsyncCallGraph", """
using System.Diagnostics;
using System.Threading.Tasks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await Task.Yield();
        Process.Start(args[0]);
    }
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "AssemblyAsyncCallGraph.dll"));
        var callGraph = methodsSlice.CallGraph!;
        var nodeIds = callGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(callGraph.Edges, edge => edge.SourceId.Contains("MoveNext", StringComparison.Ordinal));
        Assert.Contains(callGraph.Edges, edge =>
            edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlGeneratedState &&
            edge.SourceId.Contains("Program.Main", StringComparison.Ordinal) &&
            edge.TargetId.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal));
        Assert.DoesNotContain(callGraph.Edges, edge => !string.IsNullOrWhiteSpace(edge.Path) && Path.IsPathFullyQualified(edge.Path));
        Assert.Contains(callGraph.Nodes, node =>
            node.Id.Contains("Program.Main", StringComparison.Ordinal) &&
            node.Identity?.Evidence.Contains(AnalysisEvidenceKind.AssemblyIlGeneratedState) == true &&
            node.Evidence.Any(evidence => evidence.Kind == AnalysisEvidenceKind.AssemblyIlGeneratedState));
        var processReachability = Assert.Single(methodsSlice.PackageReachability!, package => package.Purl == "pkg:nuget/System.Diagnostics.Process");
        Assert.Equal("High", processReachability.Confidence);
        Assert.Contains(AnalysisEvidenceKind.AssemblyIlGeneratedState, processReachability.EvidenceKinds);
        Assert.Contains(processReachability.SourceLocations, location =>
            location.FileName == "Program.cs" &&
            location.LineNumber > 0 &&
            location.Kind == "CallGraphEdge");
        Assert.DoesNotContain(processReachability.SourceLocations, location =>
            location.Path?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(callGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void GetMethods_AssemblyOnlyIteratorStateMachine_CollapsesMoveNextToIteratorMethod()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyIteratorCallGraph", """
using System.Collections.Generic;
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        foreach (var item in Commands(args)) { }
    }

    private static IEnumerable<string> Commands(string[] args)
    {
        Process.Start(args[0]);
        yield return args[0];
    }
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "AssemblyIteratorCallGraph.dll"));
        var callGraph = methodsSlice.CallGraph!;
        var nodeIds = callGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(callGraph.Edges, edge => edge.SourceId.Contains("MoveNext", StringComparison.Ordinal));
        Assert.Contains(callGraph.Edges, edge =>
            edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlGeneratedState &&
            edge.SourceId.Contains("Program.Commands", StringComparison.Ordinal) &&
            edge.TargetId.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal));
        Assert.All(callGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void GetMethods_AssemblyOnlyDelegateInvokeAndEventAdd_ResolvesCallbackTargets()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyDelegateCallGraph", """
using System;

public sealed class Publisher
{
    public event Action? Fired;
    public void Raise() => Fired?.Invoke();
}

public static class Program
{
    public static void Main()
    {
        Action action = Handle;
        action();
        var publisher = new Publisher();
        publisher.Fired += Handle;
    }

    private static void Handle() { }
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "AssemblyDelegateCallGraph.dll"));
        var callGraph = methodsSlice.CallGraph!;
        var nodeIds = callGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(callGraph.Edges, edge =>
            edge is { EvidenceKind: AnalysisEvidenceKind.AssemblyIlDelegateTarget, CallType: CallType.DelegateInvoke } &&
            edge.SourceId.Contains("Program.Main", StringComparison.Ordinal) &&
            edge.TargetId.Contains("Program.Handle", StringComparison.Ordinal) &&
            edge.ArgumentExpressions?.Contains("delegate-invoke") == true);
        Assert.Contains(callGraph.Edges, edge =>
            edge is { EvidenceKind: AnalysisEvidenceKind.AssemblyIlDelegateTarget, CallType: CallType.EventSubscribe } &&
            edge.SourceId.Contains("Program.Main", StringComparison.Ordinal) &&
            edge.TargetId.Contains("Program.Handle", StringComparison.Ordinal) &&
            edge.ArgumentExpressions?.Contains("event-callback-target") == true);
        Assert.All(callGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void GetMethods_AssemblyOnlyDelegateTracking_HandlesInlineVarLocalOperands()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyInlineVarDelegate", $$"""
using System;

public static class Program
{
    public static void Main()
    {
{{GenerateManyLocalDeclarations(270)}}
        Action callback = Handle;
        callback();
{{GenerateManyLocalUses(270)}}
    }

    private static void Handle() { }
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "AssemblyInlineVarDelegate.dll"));

        Assert.Contains(methodsSlice.CallGraph!.Edges, edge =>
            edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDelegateTarget &&
            edge.SourceId.Contains("Program.Main", StringComparison.Ordinal) &&
            edge.TargetId.Contains("Program.Handle", StringComparison.Ordinal));
    }

    [Fact]
    public void AssemblyIlDecoders_MalformedSwitchOperands_StopWithoutLargeAllocation()
    {
        byte[] validEmptySwitch = [0x45, 0x00, 0x00, 0x00, 0x00];
        byte[] negativeSwitchCount = [0x45, 0xff, 0xff, 0xff, 0xff];
        byte[] truncatedSwitchTargets = [0x45, 0x01, 0x00, 0x00, 0x00];
        byte[] excessiveSwitchCount = [0x45, 0x01, 0x10, 0x00, 0x00];

        Assert.Equal(1, CountDecodedInstructions("Depscan.AssemblyCallGraphAnalyzer", validEmptySwitch));
        Assert.Equal(1, CountDecodedInstructions("Depscan.DataFlowAnalyzer", validEmptySwitch));
        Assert.Equal(0, CountDecodedInstructions("Depscan.AssemblyCallGraphAnalyzer", negativeSwitchCount));
        Assert.Equal(0, CountDecodedInstructions("Depscan.DataFlowAnalyzer", negativeSwitchCount));
        Assert.Equal(0, CountDecodedInstructions("Depscan.AssemblyCallGraphAnalyzer", truncatedSwitchTargets));
        Assert.Equal(0, CountDecodedInstructions("Depscan.DataFlowAnalyzer", truncatedSwitchTargets));
        Assert.Equal(0, CountDecodedInstructions("Depscan.AssemblyCallGraphAnalyzer", excessiveSwitchCount));
        Assert.Equal(0, CountDecodedInstructions("Depscan.DataFlowAnalyzer", excessiveSwitchCount));
    }

    [Fact]
    public void GetMethods_CombinedSourceAndAssembly_EmitsSharedEvidenceAndSourceAssemblyMapping()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "CombinedEvidenceFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        var command = args[0];
        Process.Start(command);
    }
}
""");
        var inputDirectory = Path.Combine(tempDirectory.Path, "combined-input");
        Directory.CreateDirectory(inputDirectory);
        File.Copy(Path.Combine(tempDirectory.Path, "CombinedEvidenceFlow", "src", "Program.cs"), Path.Combine(inputDirectory, "Program.cs"));
        File.Copy(Path.Combine(outputDirectory, "CombinedEvidenceFlow.dll"), Path.Combine(inputDirectory, "CombinedEvidenceFlow.dll"));
        File.Copy(Path.Combine(outputDirectory, "CombinedEvidenceFlow.pdb"), Path.Combine(inputDirectory, "CombinedEvidenceFlow.pdb"));

        var result = Depscan.Dosai.GetMethods(inputDirectory);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice);
        Assert.Contains(methodsSlice.CallGraph!.Edges, edge => edge.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect);
        Assert.Contains(methodsSlice.CallGraph.Edges, edge => edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect);
        var mainMapping = Assert.Single(methodsSlice.SourceAssemblyMapping!, mapping => mapping is { IsMapped: true, MemberName: "Main" } && mapping.AssemblyMetadataToken != 0);
        Assert.Equal(mainMapping.SourceId, mainMapping.SourceSignature);
        Assert.Equal(mainMapping.AssemblyId, mainMapping.AssemblySignature);
        Assert.StartsWith("CombinedEvidenceFlow", mainMapping.AssemblyName, StringComparison.Ordinal);
        Assert.Equal("CombinedEvidenceFlow.dll", mainMapping.ModuleName);
        Assert.Contains(methodsSlice.CallGraph.Edges, edge => edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect && edge.SourceId == mainMapping.SourceId);
        Assert.DoesNotContain(methodsSlice.CallGraph.Nodes, node => node.Id == mainMapping.AssemblyId);
        var mainNode = Assert.Single(methodsSlice.CallGraph.Nodes, node => node.Id == mainMapping.SourceId);
        Assert.Equal(mainMapping.SourceSignature, mainNode.Identity?.SourceSignature);
        Assert.Equal(mainMapping.AssemblySignature, mainNode.Identity?.AssemblySignature);
        Assert.Contains(AnalysisEvidenceKind.SourceRoslynDirect, mainNode.Identity!.Evidence);
        Assert.Contains(AnalysisEvidenceKind.AssemblyIlDirect, mainNode.Identity.Evidence);
        var processReachability = Assert.Single(methodsSlice.PackageReachability!, package => package.Purl == "pkg:nuget/System.Diagnostics.Process");
        Assert.Equal("High", processReachability.Confidence);
        Assert.Contains(AnalysisEvidenceKind.AssemblyIlDirect, processReachability.EvidenceKinds);
        Assert.Contains(processReachability.SourceLocations, location =>
            location.FileName == "Program.cs" &&
            location.LineNumber > 0 &&
            location.Kind == "CallGraphEdge");
        Assert.DoesNotContain(processReachability.SourceLocations, location =>
            location.Path?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void GetMethods_VBSourceMode_DoesNotAddAssemblyReflectionEvidenceToAssemblyMethods()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "VbSourceModeAssembly", "public static class Program { public static void Main() { } }");
        var inputDirectory = Path.Combine(tempDirectory.Path, "combined-vb-input");
        Directory.CreateDirectory(inputDirectory);
        File.WriteAllText(Path.Combine(inputDirectory, "OnlySource.vb"), "Public Class OnlySource\n    Public Sub Run()\n    End Sub\nEnd Class\n");
        File.Copy(Path.Combine(outputDirectory, "VbSourceModeAssembly.dll"), Path.Combine(inputDirectory, "VbSourceModeAssembly.dll"));
        File.Copy(Path.Combine(outputDirectory, "VbSourceModeAssembly.pdb"), Path.Combine(inputDirectory, "VbSourceModeAssembly.pdb"));

        var methodsSlice = ReadMethods(inputDirectory);

        Assert.Contains(methodsSlice.Methods!, method => method is { FileName: "OnlySource.vb", ClassName: "OnlySource" });
        var assemblyMethods = methodsSlice.Methods!.Where(method => method.FileName == "VbSourceModeAssembly.dll").ToList();
        Assert.NotEmpty(assemblyMethods);
        Assert.DoesNotContain(assemblyMethods, method => method.Evidence.Any(evidence => evidence.Kind == AnalysisEvidenceKind.AssemblyReflection));
        Assert.DoesNotContain(assemblyMethods, method => method.Identity?.Evidence.Contains(AnalysisEvidenceKind.AssemblyReflection) == true);
    }

    [Fact]
    public void GetMethods_CombinedSourceAndAssembly_MapsOverloadsBySignature()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "OverloadMappingFlow", """
public static class Program
{
    public static void Main()
    {
        Run("safe");
        Run(42);
    }

    public static void Run(string value) { }

    public static void Run(int value) { }
}
""");
        var inputDirectory = Path.Combine(tempDirectory.Path, "combined-overload-input");
        Directory.CreateDirectory(inputDirectory);
        File.Copy(Path.Combine(tempDirectory.Path, "OverloadMappingFlow", "src", "Program.cs"), Path.Combine(inputDirectory, "Program.cs"));
        File.Copy(Path.Combine(outputDirectory, "OverloadMappingFlow.dll"), Path.Combine(inputDirectory, "OverloadMappingFlow.dll"));
        File.Copy(Path.Combine(outputDirectory, "OverloadMappingFlow.pdb"), Path.Combine(inputDirectory, "OverloadMappingFlow.pdb"));

        var methodsSlice = ReadMethods(inputDirectory);

        var runMappings = methodsSlice.SourceAssemblyMapping!
            .Where(mapping => mapping is { IsMapped: true, MemberName: "Run" })
            .ToList();
        Assert.Equal(2, runMappings.Count);
        var stringRun = Assert.Single(runMappings, mapping => mapping.AssemblySignature?.Contains("String", StringComparison.Ordinal) == true);
        var intRun = Assert.Single(runMappings, mapping => mapping.AssemblySignature?.Contains("Int32", StringComparison.Ordinal) == true);
        Assert.Contains("String", stringRun.AssemblySignature, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", stringRun.AssemblySignature, StringComparison.Ordinal);
        Assert.Contains("Int32", intRun.AssemblySignature, StringComparison.Ordinal);
        Assert.DoesNotContain("String", intRun.AssemblySignature, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethods_VBAssembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestDataVBDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(24, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataVBDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldCSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;
        var properties = methodsSlice?.Properties;
        var fields = methodsSlice?.Fields;
        Assert.Equal(21, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        var genericProcessorClassMethods = actualMethods?.Where(m => m.ClassName == "GenericProcessor").ToList();
        Assert.NotNull(genericProcessorClassMethods);
        Assert.True(genericProcessorClassMethods?.Count == 4);
        var processMethod = genericProcessorClassMethods?.FirstOrDefault(m => m.Name == "Process");
        Assert.NotNull(processMethod);
        Assert.True(processMethod?.IsGenericMethod == false);
        Assert.True(processMethod?.Parameters?.Count == 1);
        var convertToMethod = genericProcessorClassMethods?.FirstOrDefault(m => m.Name == "ConvertTo");
        Assert.NotNull(convertToMethod);
        Assert.True(convertToMethod.IsGenericMethod); 
        Assert.Single(convertToMethod.GenericParameters!);
        Assert.Contains("TResult", convertToMethod?.GenericParameters ?? []);
        Assert.True(convertToMethod?.Parameters?.Count == 1);
        var utilityClassMethods = actualMethods?.Where(m => m.ClassName == "Utility").ToList();
        Assert.NotNull(utilityClassMethods);
        var getDefaultMethod = utilityClassMethods?.FirstOrDefault(m => m.Name == "GetDefault");
        Assert.NotNull(getDefaultMethod);
        Assert.True(getDefaultMethod?.IsGenericMethod);
        Assert.Equal(1, getDefaultMethod?.GenericParameters?.Count);
        Assert.Contains("T", getDefaultMethod?.GenericParameters ?? []);
        Assert.Equal(0, getDefaultMethod?.Parameters?.Count);
        var swapMethod = utilityClassMethods?.FirstOrDefault(m => m.Name == "Swap");
        Assert.NotNull(swapMethod);
        Assert.True(swapMethod.IsGenericMethod); 
        Assert.True(swapMethod?.GenericParameters?.Count == 1);
        Assert.Contains("T", swapMethod?.GenericParameters ?? []);
        Assert.True(swapMethod?.Parameters?.Count == 2);
        Assert.Equal("T", swapMethod?.Parameters?[0].Type);
        Assert.Equal("T", swapMethod?.Parameters?[1].Type);
        Assert.Equal("void", swapMethod?.ReturnType);
        var genericProcessorProperties = properties?.Where(p => p.ClassName == "GenericProcessor").ToList();
        Assert.NotNull(genericProcessorProperties);
        var valueProperty = genericProcessorProperties?.FirstOrDefault(p => p.Name == "Value");
        Assert.NotNull(valueProperty);
        Assert.Equal("T", valueProperty?.Type);
        Assert.Equal("T", valueProperty?.TypeFullName);
        // Test inheritance and interface implementation
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
        var getNamesMethod = helloClassMethods?.FirstOrDefault(m => m.Name == "GetNames");
        Assert.NotNull(getNamesMethod);
        Assert.Equal("System.Collections.Generic.List<string>", getNamesMethod.ReturnType);
        Assert.True(genericProcessorClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IGenericInterface")));
        
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
        
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_CSharpSource_CallGraphUsesStableNodeIdsAndValidEdges()
    {
        var sourcePath = GetFilePath(HelloWorldCSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var callGraph = methodsSlice?.CallGraph;

        Assert.NotNull(callGraph);
        Assert.NotEmpty(callGraph.Nodes);
        Assert.NotEmpty(callGraph.Edges);
        Assert.Contains(callGraph.Nodes, node => node.Identity?.Evidence.Contains(AnalysisEvidenceKind.SourceRoslynDirect) == true);
        Assert.Contains(callGraph.Edges, edge => edge.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect && edge.Evidence.Any(evidence => evidence.Kind == AnalysisEvidenceKind.SourceRoslynDirect));

        var nodeIds = callGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("HelloWorld.Hello.Appreciate():System.Threading.Tasks.Task", nodeIds);
        Assert.Contains("System.Threading.Tasks.Task.Delay(int):System.Threading.Tasks.Task", nodeIds);
        Assert.Contains("HelloWorld.GenericProcessor<T>..ctor(T)", nodeIds);
        Assert.Contains("HelloWorld.GenericProcessor<T>.set_Value(T):void", nodeIds);

        Assert.All(callGraph.Edges, edge =>
        {
            Assert.False(string.IsNullOrWhiteSpace(edge.Id));
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
            Assert.True(edge.CallLocation.LineNumber > 0);
            Assert.True(edge.CallLocation.ColumnNumber > 0);
        });

        Assert.Contains(callGraph.Edges, edge =>
            edge is { SourceId: "HelloWorld.Hello.Appreciate():System.Threading.Tasks.Task", TargetId: "System.Threading.Tasks.Task.Delay(int):System.Threading.Tasks.Task", CallType: CallType.MethodCall });
        Assert.Contains(callGraph.Edges, edge =>
            edge is { SourceId: "HelloWorld.GenericProcessor<T>..ctor(T)", TargetId: "HelloWorld.GenericProcessor<T>.set_Value(T):void", CallType: CallType.PropertySet });
    }

    [Fact]
    public void GetMethods_LanguageFrontend_CallGraphUsesLanguageFrontendEvidence()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Script.fs"), """
module Sample

let run value =
    printfn "%s" value
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);
        var edge = Assert.Single(methodsSlice.CallGraph!.Edges, edge => edge.TargetId == "external.printfn(*)");

        Assert.Equal(AnalysisEvidenceKind.LanguageFrontend, edge.EvidenceKind);
        Assert.Contains(edge.Evidence, evidence =>
            evidence is { Kind: AnalysisEvidenceKind.LanguageFrontend, Source: "language-frontend" });
        Assert.DoesNotContain(edge.Evidence, evidence => evidence.Kind == AnalysisEvidenceKind.SourceRoslynDirect);
    }

    [Fact]
    public void GetMethods_CSharpSource_AddsInterfaceDispatchCandidateEdges()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "SourceDispatch.cs"), """
interface IRunner
{
    void Run(string value);
}

class DefaultRunner : IRunner
{
    public void Run(string value) { }
}

class DispatchEntry
{
    static void Main()
    {
        IRunner runner = new DefaultRunner();
        runner.Run("hello");
    }
}
""");

        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice?.CallGraph);
        var nodeIds = methodsSlice.CallGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(methodsSlice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
        Assert.Contains(methodsSlice.CallGraph.Edges, edge =>
            edge is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate, SourceId: "DispatchEntry.Main():void", TargetId: "DefaultRunner.Run(string):void" } &&
            edge.Evidence.Any(evidence => evidence is { Kind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate, Source: "roslyn-source-inferred" }));
        Assert.Contains(methodsSlice.MethodCalls!, call => call is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynVirtualCandidate, TargetId: "DefaultRunner.Run(string):void" });
    }

    [Fact]
    public void GetMethods_CSharpSource_AddsDelegateAndEventCallbackEdges()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "SourceCallbacks.cs"), """
using System;

class EventSource
{
    public event Action? Fired;
    public void Raise() => Fired?.Invoke();
}

class CallbackEntry
{
    static void Main()
    {
        var source = new EventSource();
        source.Fired += Handle;
        Action action = Handle;
        action();
        Action lambda = () => Handle();
        lambda();
    }

    static void Handle() { }
}
""");

        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice?.CallGraph);
        var nodeIds = methodsSlice.CallGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(methodsSlice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
        Assert.Contains(methodsSlice.CallGraph.Edges, edge =>
            edge is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynDelegateTarget, CallType: CallType.EventSubscribe, SourceId: "CallbackEntry.Main():void", TargetId: "CallbackEntry.Handle():void" });
        Assert.Contains(methodsSlice.CallGraph.Edges, edge =>
            edge is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynDelegateTarget, CallType: CallType.DelegateInvoke, SourceId: "CallbackEntry.Main():void", TargetId: "CallbackEntry.Handle():void" });
        Assert.Contains(methodsSlice.CallGraph.Edges, edge =>
            edge is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynDelegateTarget, CallType: CallType.DelegateInvoke, SourceId: "CallbackEntry.Main():void" } &&
            edge.TargetId.StartsWith("CallbackEntry.", StringComparison.Ordinal) &&
            edge.TargetId != "CallbackEntry.Handle():void");
    }

    [Fact]
    public void GetMethods_SourceDirectory_IgnoresBinAndObjSourceFilesButKeepsAssemblyOutputs()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Root.cs"), "class RootOnly { static void Main() { } }");
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "bin"));
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "obj"));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "bin", "Generated.cs"), "class BinShouldBeIgnored { void Hidden() { } }");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "obj", "Generated.cs"), "class ObjShouldBeIgnored { void Hidden() { } }");

        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "ScopedAssemblyOutput", "public static class Program { public static void Main() { } }");
        File.Copy(Path.Combine(outputDirectory, "ScopedAssemblyOutput.dll"), Path.Combine(tempDirectory.Path, "bin", "ScopedAssemblyOutput.dll"), overwrite: true);
        File.Copy(Path.Combine(outputDirectory, "ScopedAssemblyOutput.deps.json"), Path.Combine(tempDirectory.Path, "bin", "ScopedAssemblyOutput.deps.json"), overwrite: true);

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "RootOnly");
        Assert.DoesNotContain(methodsSlice.Methods!, method => method.ClassName is "BinShouldBeIgnored" or "ObjShouldBeIgnored");
        Assert.Contains(methodsSlice.Methods!, method => method.FileName == "ScopedAssemblyOutput.dll");
    }

    [Fact]
    public void GetMethods_CSharpSource_AmbiguousUsingAliasType_DoesNotThrowAndKeepsNamespaceAliases()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scanDirectory = Path.Combine(tempDirectory.Path, "scan");
        Directory.CreateDirectory(scanDirectory);
        File.WriteAllText(Path.Combine(scanDirectory, "Program.cs"), """
using System;
using Collections = System.Collections;
using Project = PC.MyCompany.Project;

namespace AliasSource
{
    public class Program
    {
        public static void Main()
        {
            System.Console.WriteLine("hello");
        }
    }
}
""");

        // The alias target type ships in two assemblies under the scan directory, so the
        // alias target resolves to candidate type symbols instead of a single namespace.
        var ambiguousOne = BuildTemporaryProject(tempDirectory.Path, "AmbiguousAliasOne", "namespace PC.MyCompany { public class Project { public static void Main() { } } }");
        var ambiguousTwo = BuildTemporaryProject(tempDirectory.Path, "AmbiguousAliasTwo", "namespace PC.MyCompany { public class Project { public static void Main() { } } }");
        File.Copy(Path.Combine(ambiguousOne, "AmbiguousAliasOne.dll"), Path.Combine(scanDirectory, "AmbiguousAliasOne.dll"));
        File.Copy(Path.Combine(ambiguousTwo, "AmbiguousAliasTwo.dll"), Path.Combine(scanDirectory, "AmbiguousAliasTwo.dll"));

        var methodsSlice = ReadMethods(scanDirectory);

        var dependencies = methodsSlice.Dependencies ?? [];
        var ambiguousAlias = dependencies.Single(dependency => dependency.Name == "PC.MyCompany.Project");
        Assert.Equal("PC.MyCompany.Project", ambiguousAlias.Namespace);
        Assert.Empty(ambiguousAlias.NamespaceMembers ?? []);
        var namespaceAlias = dependencies.Single(dependency => dependency.Name == "System.Collections");
        Assert.Equal("System", namespaceAlias.Namespace);
        Assert.Contains("Generic", namespaceAlias.NamespaceMembers ?? []);
    }

    [Fact]
    public void GetMethods_VBSource_AmbiguousImportsAliasType_DoesNotThrowAndKeepsNamespaceAliases()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scanDirectory = Path.Combine(tempDirectory.Path, "scan");
        Directory.CreateDirectory(scanDirectory);
        File.WriteAllText(Path.Combine(scanDirectory, "Program.vb"), """
Imports Collections = System.Collections
Imports Project = PC.MyCompany.Project

Namespace AliasSource
    Public Class Program
        Public Shared Sub Main()
            System.Console.WriteLine("hello")
        End Sub
    End Class
End Namespace
""");

        var ambiguousOne = BuildTemporaryProject(tempDirectory.Path, "AmbiguousAliasOne", "namespace PC.MyCompany { public class Project { public static void Main() { } } }");
        var ambiguousTwo = BuildTemporaryProject(tempDirectory.Path, "AmbiguousAliasTwo", "namespace PC.MyCompany { public class Project { public static void Main() { } } }");
        File.Copy(Path.Combine(ambiguousOne, "AmbiguousAliasOne.dll"), Path.Combine(scanDirectory, "AmbiguousAliasOne.dll"));
        File.Copy(Path.Combine(ambiguousTwo, "AmbiguousAliasTwo.dll"), Path.Combine(scanDirectory, "AmbiguousAliasTwo.dll"));

        var methodsSlice = ReadMethods(scanDirectory);

        var dependencies = methodsSlice.Dependencies ?? [];
        var ambiguousAlias = dependencies.Single(dependency => dependency.Name == "PC.MyCompany.Project");
        Assert.Empty(ambiguousAlias.NamespaceMembers ?? []);
        var namespaceAlias = dependencies.Single(dependency => dependency.Name == "System.Collections");
        Assert.Contains("Generic", namespaceAlias.NamespaceMembers ?? []);
    }

    [Fact]
    public void Analysis_DeeplyNestedExpressions_CompleteWithoutStackOverflow()
    {
        using var tempDirectory = new TemporaryDirectory();
        const int nestingDepth = 5000;
        var expression = "1" + string.Concat(Enumerable.Repeat(" + 1", nestingDepth));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Deep.cs"), $$"""
namespace DeepNest
{
    public class Calculator
    {
        public int Calculate()
        {
            return {{expression}};
        }
    }
}
""");

        // A dedicated thread with a known stack size makes the regression deterministic:
        // unbounded walker recursion reliably overflows this stack instead of depending
        // on the host's default thread stack. A stack overflow kills the test process,
        // which is the loud failure mode this guard exists to prevent.
        string? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var dataFlows = ReadDataFlows(tempDirectory.Path);
                Assert.True(dataFlows.Statistics.FilesAnalyzed >= 1);
                Assert.NotNull(CryptoAnalyzer.GetCryptoAnalysis(tempDirectory.Path));
                Assert.Contains(ReadMethods(tempDirectory.Path).Methods ?? [], method => method.Name == "Calculate");
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
        }, maxStackSize: 8 * 1024 * 1024);
        worker.Start();
        worker.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void GetMethods_CSharpSource_AddsFrameworkDiAndReflectionHeuristicEdges()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "FrameworkReflection.cs"), """
using System;
using System.Reflection;

static class ServiceCollectionExtensions
{
    public static object AddSingleton<TService, TImplementation>(this object services) => services;
}

interface IService { }

class Worker : IService
{
    public Worker() { }
    public void Run() { }
}

class ReflectionTarget
{
    public ReflectionTarget() { }
    public void Run() { }
}

class Entry
{
    static void Main()
    {
        new object().AddSingleton<IService, Worker>();
        Activator.CreateInstance<ReflectionTarget>();
        typeof(ReflectionTarget).GetMethod("Run");
    }
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);
        var edges = methodsSlice.CallGraph!.Edges;

        Assert.Contains(edges, edge => edge is { EvidenceKind: AnalysisEvidenceKind.FrameworkModel, SourceId: "Entry.Main():void", TargetId: "Worker..ctor()" });
        Assert.Contains(edges, edge => edge is { EvidenceKind: AnalysisEvidenceKind.ReflectionHeuristic, SourceId: "Entry.Main():void", TargetId: "ReflectionTarget..ctor()" });
        Assert.Contains(edges, edge => edge is { EvidenceKind: AnalysisEvidenceKind.ReflectionHeuristic, SourceId: "Entry.Main():void", TargetId: "ReflectionTarget.Run():void" });
    }

    [Fact]
    public void CallGraphExporter_ExportsMermaidGraphMlAndGexf()
    {
        var sourcePath = GetFilePath(HelloWorldCSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var callGraph = methodsSlice?.CallGraph;

        Assert.NotNull(callGraph);

        var mermaid = CallGraphExporter.Export(callGraph, CallGraphExportFormat.Mermaid);
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Contains(" -->|\"MethodCall\"| ", mermaid);

        var graphMl = CallGraphExporter.Export(callGraph, CallGraphExportFormat.GraphMl);
        var graphMlDocument = XDocument.Parse(graphMl);
        Assert.Equal("graphml", graphMlDocument.Root?.Name.LocalName);
        Assert.Contains(graphMlDocument.Descendants(), element => element.Name.LocalName == "node");
        Assert.Contains(graphMlDocument.Descendants(), element => element.Name.LocalName == "edge");

        var gexf = CallGraphExporter.Export(callGraph, CallGraphExportFormat.Gexf);
        var gexfDocument = XDocument.Parse(gexf);
        Assert.Equal("gexf", gexfDocument.Root?.Name.LocalName);
        Assert.Contains(gexfDocument.Descendants(), element => element.Name.LocalName == "node");
        Assert.Contains(gexfDocument.Descendants(), element => element.Name.LocalName == "edge");
    }

    [Fact]
    public void GetDataFlows_CliSourceToProcessStart_ReturnsDetailedSliceAndExportsGraphs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var samplePath = Path.Combine(tempDirectory.Path, "FlowSample.cs");
        File.WriteAllText(samplePath, """
using System;
using System.Diagnostics;

class FlowSample
{
    static void Main(string[] args)
    {
        var cmd = args[0];
        var copy = string.Concat(cmd, "");
        Process.Start(copy);
    }
}
""");

        var resultJson = DataFlowAnalyzer.GetDataFlows(tempDirectory.Path);
        var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(dataFlowResult);
        Assert.True(dataFlowResult.Statistics.SourceCount >= 1);
        Assert.True(dataFlowResult.Statistics.SinkCount >= 1);
        Assert.True(dataFlowResult.Statistics.SliceCount >= 1);
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSource: true, Category: "cli", FileName: "FlowSample.cs", LineNumber: > 0 });
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, Category: "command", Symbol: not null } && node.Symbol.Contains("System.Diagnostics.Process.Start"));
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, Purl: "pkg:nuget/System.Diagnostics.Process" });
        Assert.Contains(dataFlowResult.Slices, slice => slice.SinkPurl == "pkg:nuget/System.Diagnostics.Process" && slice.Purls.Contains("pkg:nuget/System.Diagnostics.Process"));
        Assert.All(dataFlowResult.Edges, edge =>
        {
            Assert.Contains(dataFlowResult.Nodes, node => node.Id == edge.SourceId);
            Assert.Contains(dataFlowResult.Nodes, node => node.Id == edge.TargetId);
            Assert.False(!string.IsNullOrWhiteSpace(edge.Path) && Path.IsPathFullyQualified(edge.Path), $"Data-flow edge path should be relative or file-only: {edge.Path}");
        });

        var graphMl = DataFlowExporter.Export(dataFlowResult, DataFlowExportFormat.GraphMl);
        var graphMlDocument = XDocument.Parse(graphMl);
        Assert.Equal("graphml", graphMlDocument.Root?.Name.LocalName);
        Assert.Contains(graphMlDocument.Descendants(), element => element.Name.LocalName == "node");
        Assert.Contains(graphMlDocument.Descendants(), element => element.Name.LocalName == "edge");

        var gexf = DataFlowExporter.Export(dataFlowResult, DataFlowExportFormat.Gexf);
        var gexfDocument = XDocument.Parse(gexf);
        Assert.Equal("gexf", gexfDocument.Root?.Name.LocalName);

        var mermaid = DataFlowExporter.Export(dataFlowResult, DataFlowExportFormat.Mermaid);
        Assert.StartsWith("flowchart LR", mermaid);
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyCliSourceToProcessStart_ReturnsIlSliceWithValidEdges()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyOnlyFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        var command = string.Concat(args[0], "");
        Process.Start(command);
    }
}
""");

        var resultJson = DataFlowAnalyzer.GetDataFlows(Path.Combine(outputDirectory, "AssemblyOnlyFlow.dll"));
        var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(dataFlowResult);
        Assert.Equal(1, dataFlowResult.Statistics.FilesAnalyzed);
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSource: true, Category: "cli" } && node.Properties.TryGetValue("analysis", out var analysis) && analysis == "assembly-il");
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, Category: "command", Symbol: not null } && node.Symbol.Contains("System.Diagnostics.Process.Start", StringComparison.Ordinal));
        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        var nodeIds = dataFlowResult.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(dataFlowResult.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyInterproceduralSummary_ReplaysCalleeSinkAtCallSite()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyInterproceduralFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Launch(Wrap(args[0]));
    }

    private static string Wrap(string input) => string.Concat(input, "");

    private static void Launch(string command)
    {
        Process.Start(command);
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyInterproceduralFlow.dll"));

        Assert.Contains(dataFlowResult.Nodes, node => node is { Kind: "CallSummary", Symbol: not null } && node.Symbol.Contains("Wrap", StringComparison.Ordinal));
        Assert.Contains(dataFlowResult.Edges, edge => edge.Kind == "AssemblyInterproceduralReturn");
        Assert.Contains(dataFlowResult.Edges, edge => edge.Kind == "AssemblyInterproceduralSink");
        Assert.Contains(dataFlowResult.MethodSummaries, summary => summary is { EvidenceKind: AnalysisEvidenceKind.AssemblyIlSummary, Identity: not null });
        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    [Fact]
    public void GetDataFlows_SourceDirectory_KeepsBinAssemblyOutputs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "SourceDirectoryBinFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");
        var analysisDirectory = Path.Combine(tempDirectory.Path, "analysis-root");
        var binDirectory = Path.Combine(analysisDirectory, "bin");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(Path.Combine(analysisDirectory, "Root.cs"), "public static class Root { public static void Main() { } }");
        File.Copy(Path.Combine(outputDirectory, "SourceDirectoryBinFlow.dll"), Path.Combine(binDirectory, "SourceDirectoryBinFlow.dll"));
        File.Copy(Path.Combine(outputDirectory, "SourceDirectoryBinFlow.pdb"), Path.Combine(binDirectory, "SourceDirectoryBinFlow.pdb"));

        var dataFlowResult = ReadDataFlows(analysisDirectory);

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(dataFlowResult.Nodes, node =>
            node.Properties.TryGetValue("analysis", out var analysis) &&
            analysis == "assembly-il" &&
            node.Properties.TryGetValue("assembly", out var assembly) &&
            assembly == "SourceDirectoryBinFlow.dll");
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyCfgBranch_PreservesTaintedBranchToSink()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyCfgFlow", """
using System;
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        string command;
        if (DateTime.Now.Ticks > 0)
        {
            command = args[0];
        }
        else
        {
            command = "safe";
        }

        Process.Start(command);
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyCfgFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(dataFlowResult.Edges, edge => edge.Kind == "AssemblySinkCall");
    }

    [Fact]
    public void GetDataFlows_SourceRegexGuard_SuppressesValidatedTrueBranchOnly()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "GuardedFlow.cs"), """
using System.Diagnostics;
using System.Text.RegularExpressions;

class GuardedFlow
{
    static void Main(string[] args)
    {
        var command = args[0];
        if (Regex.IsMatch(command, "^[a-z]+$"))
        {
            Process.Start(command);
        }
        else
        {
            Process.Start(args[0]);
        }
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command", SinkArgument: "args[0]" });
        Assert.DoesNotContain(result.Slices, slice => slice is { SinkCategory: "command", SinkArgument: "command" });
    }

    [Fact]
    public void GetDataFlows_CryptoIvPattern_DoesNotTaintNonTokenSuffix()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "CryptoIvNoise.cs"), """
using System.Security.Cryptography;
using System.Text;

class CryptoIvNoise
{
    static void Main()
    {
        var deriv = "0123456789abcdef0123456789abcdef";
        var bytes = Encoding.UTF8.GetBytes(deriv);
        using var gcm = new AesGcm(bytes, 16);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path, patternPacks: "crypto");

        Assert.DoesNotContain(result.Nodes, node => node is { IsSource: true, Category: "crypto-material", Name: "deriv" });
        Assert.DoesNotContain(result.Slices, slice => slice is { SourceCategory: "crypto-material", SinkCategory: "crypto" } && slice.SinkArgument?.Contains("deriv", StringComparison.OrdinalIgnoreCase) == true);
    }

    // C# 15 union declarations are the headline language change in .NET 11, so they show up in
    // analyzed code as soon as projects move to the new target framework. The source fixture is
    // parsed with the newest accepted language version; these tests guard that the union
    // declaration is inventoried like any other type and that taint flows through the
    // pattern-bound payload locals of both switch forms.
    [Fact]
    public void GetMethods_CSharp15UnionSource_InventoriesUnionAndCaseMembers()
    {
        var methodsSlice = ReadMethods(GetFilePath("UnionTypes.cs"));

        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "Result" && method.Name == "Describe");
        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "UnionTypes" && method.Name == "Main");
        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "UnionTypes" && method.Name == "Describe");
    }

    [Fact]
    public void GetDataFlows_CSharp15UnionSwitchStatement_PropagatesTaintToCasePayload()
    {
        var result = ReadDataFlows(GetFilePath("UnionTypes.cs"));

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument?.Contains("message", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void GetDataFlows_CSharp15UnionSwitchExpression_PropagatesTaintToArmResult()
    {
        var result = ReadDataFlows(GetFilePath("UnionTypes.cs"));

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument?.Contains("command", StringComparison.Ordinal) == true);
    }

    // The `is` pattern is the third matching form and the most common one outside a switch;
    // its declared locals need the same operand-to-pattern propagation.
    [Fact]
    public void GetDataFlows_IsPatternBoundLocal_PropagatesOperandTaintToSink()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "IsPatternFlow.cs"), """
using System;
using System.Diagnostics;

class IsPatternFlow
{
    static void Main(string[] args)
    {
        object payload = args[0];
        if (payload is string command)
        {
            Process.Start(command);
        }
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument?.Contains("command", StringComparison.Ordinal) == true);
    }

    // Inspecting an assembly must not leave it locked. AssemblyLoadContext.LoadFromAssemblyPath
    // memory-maps the file and collectible contexts unload asynchronously, so on Windows the
    // analyzed build output stayed undeletable for the rest of the process; deleting the
    // inspected directory afterwards is the portable way to assert no handle survives.
    [Fact]
    public void GetMethods_AfterInspectingAssembly_LeavesNoFileLock()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "LockRelease", """
public static class Program
{
    public static void Main() => System.Console.WriteLine("hello");
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "LockRelease.dll"));
        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "Program" && method.Name == "Main");

        // Throws UnauthorizedAccessException on Windows while a mapped handle is still open.
        Directory.Delete(outputDirectory, recursive: true);
        Assert.False(Directory.Exists(outputDirectory));
    }

    // A self-contained deployment bundles the runtime directly in the application directory:
    // there is no shared/<framework>/<version> layout to walk, and the `dotnet` resolved from
    // PATH may belong to an older machine install. The probe list must lead with the bundled
    // runtime so its System.Runtime wins over an older shared one.
    [Fact]
    public void GetSharedFrameworkProbingPaths_SelfContainedRuntimeDirectory_ProbesBundledRuntimeFirst()
    {
        using var tempDirectory = new TemporaryDirectory();
        var selfContainedDir = Path.Combine(tempDirectory.Path, "publish");
        Directory.CreateDirectory(selfContainedDir);
        File.WriteAllText(Path.Combine(selfContainedDir, "System.Runtime.dll"), "stub");
        var olderVersion = $"{Math.Max(Environment.Version.Major - 2, 1)}.0.0";
        var netCoreAppRoot = Path.Combine(tempDirectory.Path, "shared", "Microsoft.NETCore.App");
        var olderVersionDir = Path.Combine(netCoreAppRoot, olderVersion);
        Directory.CreateDirectory(olderVersionDir);

        var probePaths = Depscan.Dosai.GetSharedFrameworkProbingPaths(selfContainedDir, [Path.Combine(tempDirectory.Path, "shared")]);

        Assert.NotEmpty(probePaths);
        Assert.Equal(Path.GetFullPath(selfContainedDir), probePaths[0]);
        Assert.DoesNotContain(Path.GetFullPath(olderVersionDir), probePaths);
    }

    // Framework-dependent install: the running version directory ranks first and the remaining
    // shared versions sort newest-first, so references resolve from a superset framework
    // instead of whatever directory order happened to enumerate first.
    [Fact]
    public void GetSharedFrameworkProbingPaths_FrameworkDependent_RanksRunningVersionFirstThenNewest()
    {
        using var tempDirectory = new TemporaryDirectory();
        var major = Environment.Version.Major;
        var versions = new[] { $"{major}.0.0", $"{major + 1}.0.0" };
        var netCoreAppRoot = Path.Combine(tempDirectory.Path, "shared", "Microsoft.NETCore.App");
        foreach (var version in versions)
        {
            Directory.CreateDirectory(Path.Combine(netCoreAppRoot, version));
        }
        var runningDir = Path.Combine(netCoreAppRoot, $"{major}.0.0");

        var probePaths = Depscan.Dosai.GetSharedFrameworkProbingPaths(runningDir, Enumerable.Empty<string>());

        Assert.Equal(Path.GetFullPath(runningDir), probePaths[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine(netCoreAppRoot, $"{major + 1}.0.0")), probePaths[1]);
    }

    // Shared frameworks older than the running runtime must not be probed: their older
    // System.Runtime would shadow the runtime actually hosting the process and drop types it
    // understands. Skipping them is what saves a single-file self-contained dosai (bundled
    // runtime, no loose System.Runtime.dll anywhere to probe) on a machine whose PATH only
    // offers an older dotnet: probing misses and the loader falls back to the Default
    // context's already-loaded bundled assemblies.
    [Fact]
    public void GetSharedFrameworkProbingPaths_OlderThanRunningRuntime_AreExcluded()
    {
        using var tempDirectory = new TemporaryDirectory();
        var major = Environment.Version.Major;
        var olderVersion = $"{Math.Max(major - 2, 1)}.0.0";
        var newerVersion = $"{major + 1}.0.0";
        var netCoreAppRoot = Path.Combine(tempDirectory.Path, "shared", "Microsoft.NETCore.App");
        Directory.CreateDirectory(Path.Combine(netCoreAppRoot, olderVersion));
        Directory.CreateDirectory(Path.Combine(netCoreAppRoot, newerVersion));
        var bundleDir = Path.Combine(tempDirectory.Path, "app");
        Directory.CreateDirectory(bundleDir); // no System.Runtime.dll: single-file layout

        var probePaths = Depscan.Dosai.GetSharedFrameworkProbingPaths(bundleDir, [Path.Combine(tempDirectory.Path, "shared")]);

        Assert.Contains(Path.GetFullPath(Path.Combine(netCoreAppRoot, newerVersion)), probePaths);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(netCoreAppRoot, olderVersion)), probePaths);
    }

    // The assembly pipeline must handle .NET 11 assemblies produced from union declarations,
    // whose lowered metadata shape differs from plain records and classes.
    [SkippableFact]
    public void GetMethods_CSharp15UnionAssembly_InventoriesLoweredMembers()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling a C# 15 union declaration at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "UnionAssembly", """
public sealed record Success(string Message);
public sealed record Failure(int ErrorCode);
public union Result(Success, Failure)
{
    public string Describe() => "result";
}
public static class Program
{
    public static void Main(string[] args)
    {
        Result parsed = new Success(args[0]);
        System.Console.WriteLine(parsed.Describe());
    }
}
""", targetFramework: "net11.0");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "UnionAssembly.dll"));

        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "Result" && method.Name == "Describe");
        Assert.Contains(methodsSlice.Methods!, method => method.ClassName == "Program" && method.Name == "Main");
    }

    // C# 15's non-union features (closed hierarchies, extension indexers, collection expression
    // arguments, labeled break/continue, unsafe expressions, pointer relaxations) must parse with
    // the widest accepted language version and keep the inventory and taint tracking working.
    [Fact]
    public void GetMethods_CSharp15FeatureSource_InventoriesFeatureAndExtensionMembers()
    {
        var methodsSlice = ReadMethods(GetFilePath(CSharp15FeatureSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "ClosedSwitch" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "UnionIsPattern" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "CollectionExpressionArguments" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "LabeledJumps" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "PointerRelaxations" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "ReadBootCommand" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "CSharp15Features", Name: "SafeKeywordMembers" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "SequenceExtensions", Name: "CountAtLeast" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Pet", Name: "Name" });
    }

    [Fact]
    public void GetDataFlows_CSharp15FeatureSource_PropagatesTaintThroughEveryConstruct()
    {
        var result = ReadDataFlows(GetFilePath(CSharp15FeatureSource));

        // Closed-hierarchy switch payload.
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument == "command");
        // Union `is`-pattern binding.
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument == "name");
        // Collection expression with a `with(...)` constructor argument.
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument == "names[0]");
        // Labeled break/continue does not break slice construction.
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument == "found");
        // The `safe` keyword is placement-only: taint still reaches the sink from the method.
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument == "command");
    }

    // File-based apps (`dotnet run app.cs`) carry their project model in `#:` directives and
    // their entry point in top-level statements. The directives must parse as trivia (they are
    // only accepted under the FileBasedProgram parser feature) instead of failing the file, and
    // the synthesized `<Main>$` plus the trailing declarations must all reach the inventory.
    [Fact]
    public void GetMethods_FileBasedAppSource_InventoriesEntryPointWithoutDirectiveNoise()
    {
        var methodsSlice = ReadMethods(GetFilePath(FileBasedAppSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Program", Name: "<Main>$" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Helpers", Name: "Report" });
        // The `#:` directive lines are trivia: no phantom members or calls named after them.
        Assert.DoesNotContain(methodsSlice.Methods!, method => method.Name is "property" or "package" or "sdk" or "include");
        Assert.DoesNotContain(methodsSlice.MethodCalls ?? [], call => call.CalledMethod is "property" or "package" or "sdk");
        // Local functions in the entry file still contribute calls to the graph.
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod?.Contains("Process.RunAndCaptureText", StringComparison.Ordinal) == true);
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod?.Contains("Process.StartAndForget", StringComparison.Ordinal) == true);
        // The `#:package` directive is the file-based app's NuGet reference: it surfaces as a
        // dependency the way `#r "nuget: ..."` does for F# scripts.
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { Name: "Microsoft.Extensions.Logging", Namespace: "nuget", Module: "FileBasedApp" });
    }

    [Fact]
    public void GetDataFlows_FileBasedAppSource_CarriesTaintToNet11ProcessSinks()
    {
        var result = ReadDataFlows(GetFilePath(FileBasedAppSource));

        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "command" });
        Assert.Contains(result.Nodes, node => node.IsSink && node.Symbol?.Contains("Process.RunAndCaptureText", StringComparison.Ordinal) == true);
        Assert.Contains(result.Nodes, node => node.IsSink && node.Symbol?.Contains("Process.StartAndForget", StringComparison.Ordinal) == true);
    }

    // The .NET 11 run-and-capture process helpers are command-execution sinks in both analysis
    // modes; a tainted argument must not slip past them just because the API is new.
    [Fact]
    public void GetDataFlows_Net11ProcessApiSinks_AllMatchAsCommandSinks()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Net11ProcessSinks.cs"), """
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

class Net11ProcessSinks
{
    static void Run(string command)
    {
        Process.StartAndForget(command);
        _ = Process.Run(command);
        _ = Process.RunAsync(command);
        _ = Process.RunAndCaptureText(command);
        _ = Process.RunAndCaptureTextAsync(command);
        SafeProcessHandle.Start(new ProcessStartInfo(command));
    }
}
""");

        var result = ReadDataFlows(tempDirectory.Path);

        foreach (var sink in new[] { "StartAndForget", "Run(", "RunAsync", "RunAndCaptureText", "RunAndCaptureTextAsync", "SafeProcessHandle.Start" })
        {
            Assert.Contains(result.Nodes, node => node.IsSink && node.Category == "command" && node.Symbol?.Contains(sink.Replace("(", string.Empty), StringComparison.Ordinal) == true);
        }

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "message", SinkCategory: "command" });
    }

    // The same sink set compiled: the IL interpreter must recognize the .NET 11 process helpers
    // from their metadata tokens like it does Process.Start.
    [SkippableFact]
    public void GetDataFlows_Net11ProcessApiSinks_AssemblyKeepsTaint()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling .NET 11 process APIs at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "Net11ProcessSinksAssembly", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        var command = args[0];
        Process.StartAndForget(command);
        _ = Process.Run(command);
        _ = Process.RunAndCaptureText(command);
    }
}
""", targetFramework: "net11.0");

        var result = ReadDataFlows(Path.Combine(outputDirectory, "Net11ProcessSinksAssembly.dll"));

        Assert.Contains(result.Nodes, node => node.IsSink && node.Symbol?.Contains("StartAndForget", StringComparison.Ordinal) == true);
        Assert.Contains(result.Nodes, node => node.IsSink && node.Symbol?.Contains("RunAndCaptureText", StringComparison.Ordinal) == true);
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    // Conditional compilation in C#: the parse options define the modern-net symbol family
    // (`NET`, `NET11_0`, `NETx_0_OR_GREATER`), so multi-target guards analyze as visible code
    // instead of becoming disabled text that no inventory or slice ever sees. DEBUG/TRACE and
    // the legacy families stay undefined - a Release-shaped, modern-target build.
    [Fact]
    public void GetMethods_CSharpModernNetGuard_StaysVisibleAndLegacyGuardsStayHidden()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "ModernNetGuard.cs"), """
using System.Diagnostics;

class ModernNetGuard
{
#if NET8_0_OR_GREATER
    public static void Modern(string command) => Process.Start(command);
#endif
#if DEBUG
    public static void DebugOnly(string command) => Process.Start(command);
#endif
#if NETFRAMEWORK
    public static void LegacyOnly(string command) => Process.Start(command);
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "ModernNetGuard", Name: "Modern" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { Name: "DebugOnly" or "LegacyOnly" });
    }

    [Fact]
    public void GetDataFlows_CSharpModernNetGuard_TaintFromGuardedBranchReachesSink()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "ModernNetGuard.cs"), """
using System.Diagnostics;

class ModernNetGuard
{
#if NET8_0_OR_GREATER
    public static void Modern(string command) => Process.Start(command);
#endif
}
""");

        var result = ReadDataFlows(tempDirectory.Path);

        Assert.Contains(result.Nodes, node => node is { IsSource: true, Name: "command", MethodName: "Modern" });
        Assert.Contains(result.Nodes, node => node is { IsSink: true, Category: "command" });
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "message", SinkCategory: "command" });
    }

    #region Issue 56 - attribute constructor arguments and target-framework awareness

    /// <summary>Writes a minimal SDK-style project file so TFM detection classifies the scan root.</summary>
    private static void WriteScanProjectFile(string directory, string frameworkProperty)
    {
        File.WriteAllText(Path.Combine(directory, "Scan.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    {frameworkProperty}
  </PropertyGroup>
</Project>
""");
    }

    // Shared shape: one params attribute applied with every constructor-argument form the
    // issue-#56 family collapsed or crashed on.
    private const string AttributeArgumentFixture = """
using System;

public static class Rows
{
    [Data(null)]
    public static void NullData() { }

    [Data(new object[0])]
    public static void EmptyData() { }

    [Data(new object?[] { null, 1, "" })]
    public static void MixedData() { }

    [Data(new object[] { new object[] { 1 } })]
    public static void JaggedData() { }

    [Data(new object?[] { "x" }, Note = null)]
    public static void NullNamed() { }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class DataAttribute(params object?[] values) : Attribute
{
    public string? Note { get; set; }
}
""";

    /// <summary>The ConstructorArguments element of the Data attribute on one method of the raw methods JSON.</summary>
    private static JsonElement DataAttributeArgument(JsonDocument document, string methodName)
    {
        foreach (var method in document.RootElement.GetProperty("Methods").EnumerateArray())
        {
            if (method.GetProperty("Name").GetString() != methodName)
            {
                continue;
            }
            foreach (var attribute in method.GetProperty("CustomAttributes").EnumerateArray())
            {
                if (attribute.GetProperty("Name").GetString() == "DataAttribute")
                {
                    return attribute.GetProperty("ConstructorArguments").EnumerateArray().First();
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"No DataAttribute found on method {methodName}.");
    }

    // These tests assert on the raw emitted JSON (not a deserialize round trip) so the
    // serializer policy itself is under test: a null stays null inside ConstructorArguments
    // elements and NamedArguments[].Value = null is not silently dropped by WhenWritingNull.

    // The reported crash: [Data(null)] on a params object?[] leaves Roslyn's TypedConstant.Values
    // at its default and the old FormatTypedConstant .Select threw NullReferenceException - one
    // attribute aborted the whole scan and no output was written at all.
    [Fact]
    public void GetMethods_NullArrayConstructorArgument_NoThrowAndSerializedAsNull()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Rows.cs"), AttributeArgumentFixture);

        var methodsSlice = ReadMethods(tempDirectory.Path);
        Assert.NotEmpty(methodsSlice.Methods!);

        using var document = JsonDocument.Parse(Depscan.Dosai.GetMethods(tempDirectory.Path));
        var argument = DataAttributeArgument(document, "NullData");
        Assert.True(argument.GetProperty("IsNull").GetBoolean());
        Assert.True(argument.GetProperty("IsArray").GetBoolean());
        Assert.False(argument.TryGetProperty("Elements", out _));
    }

    // An empty array and a null array reference must stay distinguishable.
    [Fact]
    public void GetMethods_EmptyArrayConstructorArgument_DistinctFromNull()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Rows.cs"), AttributeArgumentFixture);

        using var document = JsonDocument.Parse(Depscan.Dosai.GetMethods(tempDirectory.Path));
        var argument = DataAttributeArgument(document, "EmptyData");
        Assert.True(argument.GetProperty("IsArray").GetBoolean());
        Assert.False(argument.TryGetProperty("IsNull", out _));
        Assert.Equal(0, argument.GetProperty("Elements").GetArrayLength());
    }

    // A null element inside an array stays null next to scalar elements; jagged arrays recurse.
    [Fact]
    public void GetMethods_NullElementAndJaggedArrays_RecurseLosslessly()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Rows.cs"), AttributeArgumentFixture);

        using var document = JsonDocument.Parse(Depscan.Dosai.GetMethods(tempDirectory.Path));

        var mixed = DataAttributeArgument(document, "MixedData");
        Assert.Equal(3, mixed.GetProperty("Elements").GetArrayLength());
        Assert.True(mixed.GetProperty("Elements")[0].GetProperty("IsNull").GetBoolean());
        Assert.Equal("1", mixed.GetProperty("Elements")[1].GetProperty("Value").GetString());
        Assert.Equal(string.Empty, mixed.GetProperty("Elements")[2].GetProperty("Value").GetString());

        var jagged = DataAttributeArgument(document, "JaggedData");
        Assert.True(jagged.GetProperty("Elements")[0].GetProperty("IsArray").GetBoolean());
        Assert.Equal("1", jagged.GetProperty("Elements")[0].GetProperty("Elements")[0].GetProperty("Value").GetString());
    }

    // NamedArguments[].Value = null must survive serialization: under WhenWritingNull the
    // property would silently disappear, so NamedArgumentInfo.Value opts into Never.
    [Fact]
    public void GetMethods_NullNamedArgument_KeepsValuePropertyInJson()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Rows.cs"), AttributeArgumentFixture);

        ReadMethods(tempDirectory.Path);

        using var document = JsonDocument.Parse(Depscan.Dosai.GetMethods(tempDirectory.Path));
        foreach (var method in document.RootElement.GetProperty("Methods").EnumerateArray())
        {
            if (method.GetProperty("Name").GetString() != "NullNamed")
            {
                continue;
            }
            foreach (var attribute in method.GetProperty("CustomAttributes").EnumerateArray())
            {
                if (attribute.GetProperty("Name").GetString() != "DataAttribute")
                {
                    continue;
                }
                var named = attribute.GetProperty("NamedArguments").EnumerateArray().Single();
                Assert.Equal("Note", named.GetProperty("Name").GetString());
                Assert.True(named.TryGetProperty("Value", out var value));
                Assert.Equal(JsonValueKind.Null, value.ValueKind);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("No DataAttribute with named arguments found on NullNamed.");
    }

    // The assembly (MetadataLoadContext) path must produce the same encoding as the Roslyn
    // path for the same attribute: it used to stringify arrays as ReadOnlyCollection type names.
    [Fact]
    public void GetMethods_Net8Assembly_AttributeArgumentsAgreeWithSourceScan()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "Net8AttributeArgs", """
using System;

public static class Rows
{
    [Data(null)]
    public static void NullData() { }

    [Data(new object[0])]
    public static void EmptyData() { }

    [Data(new object?[] { null, 1, "" })]
    public static void MixedData() { }

    [Data(new object[] { new object[] { 1 } })]
    public static void JaggedData() { }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class DataAttribute(params object?[] values) : Attribute;
""", targetFramework: "net8.0", outputType: "Library");

        var sourceJson = Depscan.Dosai.GetMethods(Path.Combine(tempDirectory.Path, "Net8AttributeArgs", "src"));
        var assemblyJson = Depscan.Dosai.GetMethods(outputDirectory);
        Assert.Equal(AttributeArgumentShapes(sourceJson), AttributeArgumentShapes(assemblyJson));
    }

    /// <summary>Per-method shape of the Data attribute argument, ignoring Type display strings (Roslyn display text vs reflection names differ by design).</summary>
    private static SortedList<string, string> AttributeArgumentShapes(string methodsJson)
    {
        using var document = JsonDocument.Parse(methodsJson);
        var shapes = new SortedList<string, string>();
        foreach (var method in document.RootElement.GetProperty("Methods").EnumerateArray())
        {
            var methodName = method.GetProperty("Name").GetString();
            foreach (var attribute in method.GetProperty("CustomAttributes").EnumerateArray())
            {
                if (attribute.GetProperty("Name").GetString() == "DataAttribute" && !shapes.ContainsKey(methodName!))
                {
                    shapes.Add(methodName!, Shape(attribute.GetProperty("ConstructorArguments").EnumerateArray().First()));
                }
            }
        }

        return shapes;

        static string Shape(JsonElement argument)
        {
            if (argument.TryGetProperty("Elements", out var elements))
            {
                return "[" + string.Join(",", elements.EnumerateArray().Select(Shape)) + "]";
            }
            if (argument.TryGetProperty("IsNull", out var isNull) && isNull.GetBoolean())
            {
                return "null";
            }
            return argument.TryGetProperty("Value", out var value) ? value.GetString() ?? "null" : "?";
        }
    }

    // A symbol whose attribute extraction throws must cost only its own attribute list: the
    // helper degrades to empty and reports into Diagnostics instead of aborting the scan.
    [Fact]
    public void ExtractCustomAttributes_HostileSymbol_DegradesToEmptyWithDiagnostic()
    {
        var diagnostics = new List<string>();
        var attributes = Depscan.Dosai.ExtractCustomAttributes(new ThrowingSymbol(), diagnostics);

        Assert.Empty(attributes);
        Assert.Single(diagnostics);
        Assert.Contains("HostileSymbol", diagnostics[0], StringComparison.Ordinal);
        Assert.Contains("Attribute extraction failed", diagnostics[0], StringComparison.Ordinal);
    }

    // Roslyn's PublicAPI analyzer forbids external ISymbol implementations (RS1009); this
    // test double is exactly the sanctioned use case for it.
#pragma warning disable RS1009
    private sealed class ThrowingSymbol : ISymbol
    {
        public string Name => "HostileSymbol";
        public ImmutableArray<AttributeData> GetAttributes() => throw new NullReferenceException("simulated malformed attribute data");
        public SymbolKind Kind => SymbolKind.Method;
        public string Language => "C#";
        public string MetadataName => Name;
        public ISymbol? ContainingSymbol => null;
        public IAssemblySymbol? ContainingAssembly => null;
        public IModuleSymbol? ContainingModule => null;
        public INamedTypeSymbol? ContainingType => null;
        public INamespaceSymbol? ContainingNamespace => null;
        public bool IsDefinition => false;
        public bool IsStatic => false;
        public bool IsVirtual => false;
        public bool IsOverride => false;
        public bool IsAbstract => false;
        public bool IsSealed => false;
        public bool IsExtern => false;
        public bool IsImplicitlyDeclared => false;
        public bool CanBeReferencedByName => false;
        public bool HasUnsupportedMetadata => false;
        public ImmutableArray<Location> Locations => [];
        public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [];
        public Accessibility DeclaredAccessibility => Accessibility.NotApplicable;
        public ISymbol OriginalDefinition => this;
        public int MetadataToken => 0;
        public bool Equals(ISymbol? other) => ReferenceEquals(this, other);
        public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => ReferenceEquals(this, other);
        public void Accept(SymbolVisitor visitor) => visitor.Visit(this);
        public TResult? Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.Visit(this);
        public TResult Accept<TArgument, TResult>(SymbolVisitor<TArgument, TResult> visitor, TArgument argument) => visitor.Visit(this, argument)!;
        public string ToDisplayString(SymbolDisplayFormat? format = null) => Name;
        public ImmutableArray<SymbolDisplayPart> ToDisplayParts(SymbolDisplayFormat? format = null) => [];
        public string ToMinimalDisplayString(SemanticModel semanticModel, int position, SymbolDisplayFormat? format = null) => Name;
        public ImmutableArray<SymbolDisplayPart> ToMinimalDisplayParts(SemanticModel semanticModel, int position, SymbolDisplayFormat? format = null) => [];
        public string? GetDocumentationCommentId() => null;
        public string GetDocumentationCommentXml(CultureInfo? preferredCulture = null, bool expand = false, CancellationToken cancellationToken = default) => string.Empty;
    }
#pragma warning restore RS1009

    [Fact]
    public void GetMethods_Net8Project_AnalyzesNet8GuardBodiesAndElseArm()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFramework>net8.0</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), """
TfmGuards.Run();

public static class TfmGuards
{
#if NET8_0
    public static void Net8Only() { }
#endif
#if NET9_0_OR_GREATER
    public static void Net9Plus() { }
#else
    public static void Net8Fallback() { }
#endif
    public static void Run()
    {
        Net8Only();
        Net8Fallback();
    }
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        // Both net8 code paths are analyzed: the exact-TFM guard body and the #else arm of a
        // guard the project would never compile. On the old hardcoded latest-net symbols the
        // two net8 bodies were disabled text and only Net9Plus existed.
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "TfmGuards", Name: "Net8Only" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "TfmGuards", Name: "Net8Fallback" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "TfmGuards", Name: "Net9Plus" });

        // They are call-graph edges from the entry chain, not dead code.
        Assert.Contains(methodsSlice.CallGraph!.Edges, edge => edge.SourceId.Contains("TfmGuards.Run()", StringComparison.Ordinal) && edge.TargetId.Contains("Net8Only()", StringComparison.Ordinal));
        Assert.Contains(methodsSlice.CallGraph!.Edges, edge => edge.SourceId.Contains("TfmGuards.Run()", StringComparison.Ordinal) && edge.TargetId.Contains("Net8Fallback()", StringComparison.Ordinal));
        Assert.DoesNotContain(methodsSlice.DeadCode ?? [], entry => entry.Name is "Net8Only" or "Net8Fallback");

        // What was assumed is visible to consumers.
        Assert.Equal("net8.0", Assert.Single(methodsSlice.Metadata!.TargetFrameworks!));
    }

    [Fact]
    public void GetMethods_NetStandard20Project_AnalyzesNetStandardGuardBody()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFramework>netstandard2.0</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if NETSTANDARD2_0
    public static void StandardOnly() { }
#endif
#if NETFRAMEWORK
    public static void FrameworkOnly() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "StandardOnly" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "FrameworkOnly" });
        Assert.Equal("netstandard2.0", Assert.Single(methodsSlice.Metadata!.TargetFrameworks!));
    }

    [Fact]
    public void GetMethods_Net472Project_AnalyzesFrameworkGuardBody()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFramework>net472</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if NETFRAMEWORK
    public static void FrameworkOnly() { }
#endif
#if NET
    public static void ModernOnly() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "FrameworkOnly" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "ModernOnly" });
        Assert.Equal("net472", Assert.Single(methodsSlice.Metadata!.TargetFrameworks!));
    }

    // A multi-target project resolves to one representative target - the most modern it
    // declares - so the analyzed arms are a real compilation rather than a mix no build
    // produces. Every detected target still reaches the metadata, and the representative is
    // named there and in a Diagnostics note because it decides what is in the results.
    [Fact]
    public void GetMethods_MultiTargetProject_AnalyzesRepresentativeTargetGuards()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFrameworks>net462;net8.0;net10.0</TargetFrameworks>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if NET462
    public static void Net462Only() { }
#endif
#if NET8_0
    public static void Net8Only() { }
#endif
#if NET10_0_OR_GREATER
    public static void Net10Plus() { }
#endif
#if NETSTANDARD2_0
    public static void StandardOnly() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net10Plus" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net462Only" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net8Only" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "StandardOnly" });
        Assert.Equal(["net462", "net8.0", "net10.0"], methodsSlice.Metadata!.TargetFrameworks);
        Assert.Equal("net10.0", methodsSlice.Metadata!.GuardTargetFramework);
        Assert.Contains(methodsSlice.Diagnostics ?? [], diagnostic => diagnostic.Contains("evaluated against 'net10.0'", StringComparison.Ordinal));
    }

    // The defect a union of symbol sets produces, and the reason this resolves to one target
    // instead: Hangfire.Core targets net451;net46;netstandard1.3;netstandard2.0, and its
    // `#if !NETSTANDARD1_3` members ship in three of those four assemblies. Defining
    // NETSTANDARD1_3 because one target defines it erased them from the inventory and the call
    // graph - a public API present in the shipped binaries, invisible to analysis. Negated
    // guards are the most common shape in real multi-target libraries, so this is the case that
    // decides the semantics.
    [Fact]
    public void GetMethods_MultiTargetProjectWithNegatedGuard_KeepsMembersCompiledByMostTargets()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFrameworks>net451;net46;netstandard1.3;netstandard2.0</TargetFrameworks>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if !NETSTANDARD1_3
    public static void UseElmahLogProvider() { }
#endif
#if NETSTANDARD1_3
    public static void Standard13Only() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Equal("netstandard2.0", methodsSlice.Metadata!.GuardTargetFramework);
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "UseElmahLogProvider" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Standard13Only" });
    }

    // A classic, non-SDK project declares its target as TargetFrameworkVersion rather than a
    // TFM. Without that shape the project detects as nothing and silently inherits the
    // modern-net fallback - the one case where the fallback is knowably wrong.
    [Fact]
    public void GetMethods_ClassicTargetFrameworkVersionProject_DetectsNetFramework()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if NETFRAMEWORK
    public static void FrameworkOnly() { }
#endif
#if NET11_0
    public static void ModernOnly() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Equal("net472", Assert.Single(methodsSlice.Metadata!.TargetFrameworks!));
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "FrameworkOnly" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "ModernOnly" });
    }

    // A project file copied into build output must not widen detection: bin/obj are skipped
    // for project files, the same way BuildPreparation skips them for restore targets.
    [Fact]
    public void GetMethods_ProjectFileUnderBuildDirectory_IsIgnoredByDetection()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFramework>net8.0</TargetFramework>");
        var staleOutput = Path.Combine(tempDirectory.Path, "bin", "Debug");
        Directory.CreateDirectory(staleOutput);
        File.WriteAllText(Path.Combine(staleOutput, "Stale.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup></Project>");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Equal("net8.0", Assert.Single(methodsSlice.Metadata!.TargetFrameworks!));
    }

    // The regression most likely to slip: with no project file anywhere under the root, the
    // fallback stays exactly the old hardcoded latest-modern-net set (NET11_0 defined, NET8_0
    // exact not, NETFRAMEWORK not), the metadata carries no TargetFrameworks, and a
    // Diagnostics note says the default was assumed.
    [Fact]
    public void GetMethods_NoProjectFile_KeepsLatestModernNetFallbackBehavior()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if NET11_0
    public static void Net11Only() { }
#endif
#if NET8_0
    public static void Net8ExactOnly() { }
#endif
#if NET8_0_OR_GREATER
    public static void Net8Plus() { }
#endif
#if NETFRAMEWORK
    public static void FrameworkOnly() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net11Only" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net8Plus" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net8ExactOnly" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "FrameworkOnly" });
        Assert.Null(methodsSlice.Metadata!.TargetFrameworks);
        Assert.Contains(methodsSlice.Diagnostics ?? [], diagnostic => diagnostic.Contains("No TargetFramework detected", StringComparison.Ordinal));
    }

    // Assembly-only trees have no project file; the runtimeconfig names the framework.
    [Fact]
    public void GetMethods_RuntimeConfigOnlyDirectory_DetectsTargetFramework()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "App.runtimeconfig.json"), """
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "8.0.0"
    }
  }
}
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Guards.cs"), """
public static class Guards
{
#if NET8_0
    public static void Net8Only() { }
#endif
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Guards", Name: "Net8Only" });
        Assert.Equal("net8.0", Assert.Single(methodsSlice.Metadata!.TargetFrameworks!));
    }

    // The F# line frontend evaluates the same detected define set as the C# pipeline.
    [Fact]
    public void GetMethods_FSharpNet8Project_AnalyzesNet8GuardedBodies()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFramework>net8.0</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "App.fs"), """
module Sample.App

#if NET8_0
let net8Only () = ()
#endif
#if NET9_0_OR_GREATER
let net9Plus () = ()
#else
let net8Fallback () = ()
#endif
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Methods!, method => method is { Module: "LanguageFrontend", Name: "net8Only" });
        Assert.Contains(methodsSlice.Methods!, method => method is { Module: "LanguageFrontend", Name: "net8Fallback" });
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { Module: "LanguageFrontend", Name: "net9Plus" });
    }

    [Fact]
    public void GetDataFlows_Net8ProjectGuardedBranch_TaintReachesSink()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteScanProjectFile(tempDirectory.Path, "<TargetFramework>net8.0</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), """
using System.Diagnostics;

public static class Guarded
{
#if NET8_0
    public static void Modern(string command) => Process.Start(command);
#endif
}
""");

        var result = ReadDataFlows(tempDirectory.Path);

        Assert.Contains(result.Nodes, node => node is { IsSource: true, Name: "command", MethodName: "Modern" });
        Assert.Contains(result.Nodes, node => node is { IsSink: true, Category: "command" });
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "message", SinkCategory: "command" });
    }

    #endregion

    // ASP.NET Core 11: `[ShortCircuit]` (on a minimal-API lambda) must not hide the endpoint,
    // and a union-typed JSON body parameter must be seeded as an HTTP source so taint from the
    // deserialized union reaches sinks.
    [Fact]
    public void GetMethods_AspNetCore11Endpoints_ExtractShortCircuitAndUnionRoutes()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "AspNet11.cs"), """
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", [ShortCircuit] () => "ok");
app.MapPost("/pets", (PetUnion pet, CancellationToken ct) => Results.Ok(pet));
app.Run();

public sealed record Cat(string Name);
public sealed record Dog(string Name);
public union PetUnion(Cat, Dog);
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { HttpMethod: "GET", Path: "/health", EndpointKind: "MinimalApi" });
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { HttpMethod: "POST", Path: "/pets", EndpointKind: "MinimalApi" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Program", Name: "<Main>$" });
    }

    [Fact]
    public void GetDataFlows_AspNetCore11UnionBodyLambda_SeedsHandlerParameter()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "AspNet11Union.cs"), """
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapPost("/pets", (PetUnion pet, CancellationToken ct) =>
{
    var name = pet switch { Cat c => c.Name, Dog d => d.Name };
    Process.Start(name);
    return Results.Ok(name);
});
app.Run();

public sealed record Cat(string Name);
public sealed record Dog(string Name);
public union PetUnion(Cat, Dog);
""");

        var result = ReadDataFlows(tempDirectory.Path);

        // The union body parameter is bound from the request; the cancellation token is not.
        Assert.Contains(result.Nodes, node => node.IsSource && node.Category == "http" && node.Name == "pet" && node.Type?.Contains("PetUnion", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Nodes, node => node.IsSource && node.Name is "ct" or "cancellationToken");
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "http", SinkCategory: "command" });
    }

    // Extension-member declarations live in a compiler-synthesized nested type whose metadata
    // name is empty; the inventory must attribute them to the enclosing static class instead of
    // reporting a blank class name.
    [Fact]
    public void GetMethods_ExtensionBlockMembers_AttributeToContainingStaticClass()
    {
        var methodsSlice = ReadMethods(GetFilePath(ExtensionMemberSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "SequenceHelpers", Name: "CountAtLeast" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "SequenceHelpers", Name: "get_Count" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "SequenceHelpers", Name: "DoubleCount" });
        Assert.All(methodsSlice.Methods!, method => Assert.False(string.IsNullOrWhiteSpace(method.ClassName)));
    }

    [Fact]
    public void GetMethods_ExtensionBlockIndexerUse_AppearsInCallGraph()
    {
        var methodsSlice = ReadMethods(GetFilePath(ExtensionMemberSource));

        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod is not null && call.CalledMethod.Contains("this[int]", StringComparison.Ordinal));
        var nodeIds = methodsSlice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(methodsSlice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    // A compiled closed-hierarchy switch lowers to `isinst` + payload reads; the IL interpreter
    // must keep taint through the type test instead of dropping it on the cast.
    [SkippableFact]
    public void GetDataFlows_ClosedHierarchyAssembly_TypeTestKeepsTaint()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling a C# 15 closed hierarchy at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "ClosedHierarchyAssembly", """
public closed record class GateState;
public record class GateClosed : GateState;
public record class GateOpen(string Command) : GateState;
public static class Program
{
    public static void Main(string[] args)
    {
        GateState state = new GateOpen(args[0]);
        var command = state switch
        {
            GateClosed => string.Empty,
            GateOpen(var cmd) => cmd,
        };
        System.Diagnostics.Process.Start(command);
    }
}
""", targetFramework: "net11.0");

        var result = ReadDataFlows(Path.Combine(outputDirectory, "ClosedHierarchyAssembly.dll"));

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    // Positional patterns (`GateOpen(var cmd)`) lower to a Deconstruct call with an out local;
    // the interpreter must write the callee-side taint back through the by-ref slot.
    [SkippableFact]
    public void GetDataFlows_PositionalPatternAssembly_OutParameterReceivesTaint()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling a positional pattern at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "PositionalPatternAssembly", """
public record class GateOpen(string Command);
public static class Program
{
    public static void Main(string[] args)
    {
        object state = new GateOpen(args[0]);
        if (state is GateOpen(var cmd))
        {
            System.Diagnostics.Process.Start(cmd);
        }
    }
}
""", targetFramework: "net11.0");

        var result = ReadDataFlows(Path.Combine(outputDirectory, "PositionalPatternAssembly.dll"));

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    [SkippableFact]
    public void GetDataFlows_ObjectDowncastAssembly_KeepsTaint()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling the downcast sample at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "ObjectDowncastAssembly", """
public static class Program
{
    public static void Main(string[] args)
    {
        object value = args[0];
        System.Diagnostics.Process.Start((string)value);
    }
}
""", targetFramework: "net11.0");

        var result = ReadDataFlows(Path.Combine(outputDirectory, "ObjectDowncastAssembly.dll"));

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    // The full C# 15 feature mix in one compiled assembly: closed hierarchy, union is-pattern,
    // collection expression arguments, extension indexer use, and labeled break/continue.
    [SkippableFact]
    public void GetDataFlows_CSharp15FeatureAssembly_PropagatesTaintThroughAllConstructs()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling C# 15 features at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "CSharp15Assembly", """
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public sealed record Cat(string Name);
public union Pet(Cat);
public closed record class GateState;
public record class GateClosed : GateState;
public record class GateOpen(string Command) : GateState;

public static class SequenceExtensions
{
    extension(IEnumerable<string> sequence)
    {
        public string this[int index] => sequence.ElementAt(index);
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        GateState state = new GateOpen(args[0]);
        var command = state switch
        {
            GateClosed => string.Empty,
            GateOpen(var cmd) => cmd,
        };
        Process.Start(command);

        Pet pet = new Cat(args[0]);
        if (pet is Cat(var name))
        {
            Process.Start(name);
        }

        List<string> names = [with(capacity: args.Length * 2), .. args];
        Process.Start(names[0]);

        outer: for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == "x") continue outer;
            if (names[i] == "y") break outer;
        }
    }
}
""", targetFramework: "net11.0");

        var result = ReadDataFlows(Path.Combine(outputDirectory, "CSharp15Assembly.dll"));

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(result.Edges, edge => edge.Kind == "AssemblySinkCall");
    }

    // Extension indexers compile into a compiler-generated container; the assembly inventory
    // must still expose the lowered getter and the use site must resolve to it.
    [SkippableFact]
    public void GetMethods_CSharp15ExtensionIndexerAssembly_InventoriesLoweredMembers()
    {
        Skip.IfNot(HasNet11Sdk(), "Compiling a C# 15 extension indexer at test time needs a .NET 11 SDK.");
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "ExtensionIndexerAssembly", """
using System.Collections.Generic;
using System.Linq;

public static class SequenceExtensions
{
    extension(IEnumerable<string> sequence)
    {
        public string this[int index] => sequence.ElementAt(index);
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        IEnumerable<string> names = Enumerable.Range(1, 10).Select(i => i.ToString());
        System.Console.WriteLine(names[2]);
    }
}
""", targetFramework: "net11.0");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "ExtensionIndexerAssembly.dll"));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "SequenceExtensions", Name: "get_Item" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Program", Name: "Main" });
    }

    // Binary operators (here `args.Length + 1`) must keep taint in compiled code like they do
    // in source mode; the `add`-family opcodes would otherwise pop both operand taints.
    [Fact]
    public void GetDataFlows_ArithmeticExpressionAssembly_KeepsTaintThroughBinaryOperators()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyArithmeticFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        int count = args.Length + 1;
        Process.Start(count.ToString());
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyArithmeticFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    // Analyzing one file must survive a hostile sibling tree: the best-effort discovery skips
    // the unreadable subtree, keeps enumerating its siblings, and warns instead of letting an
    // inaccessible (or over-long) directory crash the whole scan.
    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    [SkippableFact]
    public void GetDataFlows_SingleFileWithUnreadableSiblingDirectory_RemainsBestEffort()
    {
        Skip.If(OperatingSystem.IsWindows(), "The unreadable-directory setup relies on Unix permission bits.");

        using var tempDirectory = new TemporaryDirectory();
        var locked = Path.Combine(tempDirectory.Path, "locked");
        Directory.CreateDirectory(locked);
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "after"));
        File.WriteAllText(Path.Combine(locked, "hidden.cs"), "// unreachable");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "after", "reachable.cs"), "// ok");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            // Root (the default in SDK container images) bypasses permission bits, so the
            // lock is only meaningful when the mode is actually enforced for this user.
            var permissionsEnforced = !File.Exists(Path.Combine(locked, "hidden.cs"));
            if (permissionsEnforced)
            {
                // Partial discovery is observable: the sibling enumerated after the locked
                // subtree is still found, which distinguishes skip-and-continue from
                // abort-on-error.
                var discovered = SafeFileRead.EnumerateAllFilesSafe(tempDirectory.Path);
                Assert.Contains(discovered, file => file.EndsWith("Program.cs", StringComparison.Ordinal));
                Assert.Contains(discovered, file => file.EndsWith(Path.Combine("after", "reachable.cs"), StringComparison.Ordinal));
                Assert.DoesNotContain(discovered, file => file.EndsWith("hidden.cs", StringComparison.Ordinal));
            }

            var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

            Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        }
        finally
        {
            new DirectoryInfo(locked).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }
    }

    // Linked source directories are a normal repository layout, so discovery follows them; only
    // a link that re-enters a directory already walked is skipped. Pruning every link silently
    // dropped the files behind it.
    [SkippableFact]
    public void GetMethods_SourceBehindDirectorySymlink_IsStillAnalyzed()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var tempDirectory = new TemporaryDirectory();
        var real = Path.Combine(tempDirectory.Path, "real");
        var tree = Path.Combine(tempDirectory.Path, "tree");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(real, "Linked.cs"), """
public static class Linked
{
    public static string Run(string value) => value;
}
""");
        Directory.CreateSymbolicLink(Path.Combine(tree, "linked"), real);

        var methodsSlice = ReadMethods(tree);

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Linked", Name: "Run" });
    }

    // A link pointing back at an ancestor is a cycle; enumeration must terminate and still
    // report each file once.
    [SkippableFact]
    public void EnumerateAllFilesSafe_SymlinkCycle_TerminatesWithoutRepeating()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var tempDirectory = new TemporaryDirectory();
        var nested = Path.Combine(tempDirectory.Path, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Cycle.cs"), "// cycle");
        Directory.CreateSymbolicLink(Path.Combine(nested, "loop"), tempDirectory.Path);

        var discovered = SafeFileRead.EnumerateAllFilesSafe(tempDirectory.Path).ToList();

        Assert.Equal(1, discovered.Count(file => file.EndsWith("Cycle.cs", StringComparison.Ordinal)));
    }

    // Discovery warnings must not reach stdout: the MCP server writes line-delimited JSON-RPC
    // there, and a warning line in that stream is a protocol error for strict clients.
    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    [SkippableFact]
    public void EnumerateAllFilesSafe_UnreadableDirectory_WarnsOnStandardErrorOnly()
    {
        Skip.If(OperatingSystem.IsWindows(), "The unreadable-directory setup relies on Unix permission bits.");

        using var tempDirectory = new TemporaryDirectory();
        var locked = Path.Combine(tempDirectory.Path, $"locked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "hidden.cs"), "// unreachable");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        Skip.If(File.Exists(Path.Combine(locked, "hidden.cs")), "Permission bits are not enforced for this user (root).");
        var capturedOut = new StringWriter();
        var capturedError = new StringWriter();
        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            try
            {
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                _ = SafeFileRead.EnumerateAllFilesSafe(tempDirectory.Path).ToList();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                new DirectoryInfo(locked).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }
        }

        Assert.Contains(locked, capturedError.ToString(), StringComparison.Ordinal);
        // The unique directory name keeps this assertion immune to output from other tests.
        Assert.DoesNotContain(locked, capturedOut.ToString(), StringComparison.Ordinal);
    }

    // Extension-block members reach the call graph through their use sites; the synthesized
    // container's empty metadata name must not leak into node or identity class names, which
    // stayed blank for extension indexers even after the inventory was fixed.
    [Fact]
    public void GetMethods_ExtensionBlockIndexerUse_CallGraphNodeKeepsContainingClassName()
    {
        var methodsSlice = ReadMethods(GetFilePath(ExtensionMemberSource));

        var indexerNodes = methodsSlice.CallGraph!.Nodes.Where(node => node.Name == "get_Item").ToList();
        Assert.NotEmpty(indexerNodes);
        Assert.All(indexerNodes, node =>
        {
            Assert.Equal("SequenceHelpers", node.ClassName);
            Assert.Equal("SequenceHelpers", node.Identity?.ClassName);
        });
        Assert.All(methodsSlice.CallGraph.Nodes.Where(node => !node.IsExternal), node => Assert.False(string.IsNullOrWhiteSpace(node.ClassName)));
    }

    // Unary operators (`-x`, `~flags`) are one-to-one stack opcodes in IL; treating them as
    // two-operand arithmetic misaligns the abstract stack for the rest of the basic block.
    [Fact]
    public void GetDataFlows_UnaryOperatorAssembly_KeepsTaintAndStackAlignment()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyUnaryFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        int count = args.Length;
        int negated = -count;
        int complemented = ~negated;
        Process.Start(complemented.ToString());
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyUnaryFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    // A validator matched as a sanitizer only reads its by-ref argument; writing the sanitized
    // (null) taint back through the address would erase the local's real taint for later sinks.
    [Fact]
    public void GetDataFlows_SanitizerWithRefParameter_DoesNotClobberArgumentTaint()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyRefSanitizerFlow", """
using System.Diagnostics;

public static class Program
{
    static bool TryParse(string candidate, ref string normalized)
    {
        normalized = candidate.Trim();
        return normalized.Length > 0;
    }

    public static void Main(string[] args)
    {
        var value = args[0];
        var parsed = TryParse("constant", ref value);
        System.Diagnostics.Debug.WriteLine(parsed);
        Process.Start(value);
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyRefSanitizerFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyExceptionRegion_PropagatesThrownTaintToCatchHandler()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyExceptionFlow", """
using System;
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        try
        {
            throw new Exception(args[0]);
        }
        catch (Exception ex)
        {
            Process.Start(ex.Message);
        }
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyExceptionFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(dataFlowResult.Edges, edge => edge.Kind == "AssemblySinkCall");
    }

    [Fact]
    public void GetMethods_AssemblyOnlyGenericSignatures_DecodeConstructedTypes()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyGenericSignatures", """
using System.Collections.Generic;

public static class Program
{
    public static void Main()
    {
        var values = Echo(new List<string> { "a" });
    }

    private static List<T> Echo<T>(List<T> values) => values;
}
""");

        var methodsSlice = ReadMethods(Path.Combine(outputDirectory, "AssemblyGenericSignatures.dll"));

        Assert.Contains(methodsSlice.CallGraph!.Nodes, node => node.Id.Contains("System.Collections.Generic.List", StringComparison.Ordinal) && node.Id.Contains("string", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methodsSlice.CallGraph.Edges, edge => edge.TargetId.Contains("Program.Echo", StringComparison.Ordinal));
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyWithPortablePdb_UsesSourceLocations()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyPdbFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        var command = args[0];
        Process.Start(command);
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyPdbFlow.dll"));

        Assert.Contains(dataFlowResult.Nodes, node => node.Properties.TryGetValue("analysis", out var analysis) && analysis == "assembly-il" && node is { FileName: "Program.cs", LineNumber: > 1 });
        Assert.Contains(dataFlowResult.Nodes, node => node.MethodIdentity?.Evidence.Contains(AnalysisEvidenceKind.AssemblyIlDirect) == true && node.Evidence.Any(evidence => evidence.Kind == AnalysisEvidenceKind.AssemblyIlDirect));
        Assert.Contains(dataFlowResult.Nodes, node => node is { Kind: "Assignment", Name: "command" } && node.Properties.TryGetValue("analysis", out var analysis) && analysis == "assembly-il");
        Assert.Contains(dataFlowResult.Edges, edge => edge is { FileName: "Program.cs", LineNumber: > 1 });
        Assert.DoesNotContain(dataFlowResult.Edges, edge => !string.IsNullOrWhiteSpace(edge.Path) && Path.IsPathFullyQualified(edge.Path));
    }

    [Fact]
    public void GetDataFlows_AssemblyOnly_HandlesInlineVarLocalOperands()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyInlineVarDataFlow", $$"""
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
{{GenerateManyLocalDeclarations(270)}}
        var command = args[0];
        Process.Start(command);
{{GenerateManyLocalUses(270)}}
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyInlineVarDataFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(dataFlowResult.Nodes, node => node is { Kind: "Assignment", Name: "command" });
        Assert.Contains(dataFlowResult.Slices, slice => slice is { SinkCategory: "command", SinkArgument: "arg0" });
    }

    [Fact]
    public void GetDataFlows_AssemblyDirectory_DoesNotMergeNodesAcrossAssemblies()
    {
        using var tempDirectory = new TemporaryDirectory();
        var firstOutput = BuildTemporaryProject(tempDirectory.Path, "AssemblyNodeScopeOne", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");
        var secondOutput = BuildTemporaryProject(tempDirectory.Path, "AssemblyNodeScopeTwo", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");
        var inputDirectory = Path.Combine(tempDirectory.Path, "assembly-node-scope-input");
        Directory.CreateDirectory(inputDirectory);
        foreach (var outputDirectory in new[] { firstOutput, secondOutput })
        {
            var assemblyName = Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(outputDirectory, "*.dll").Single(file => Path.GetFileName(file).StartsWith("AssemblyNodeScope", StringComparison.Ordinal)));
            File.Copy(Path.Combine(outputDirectory, assemblyName + ".dll"), Path.Combine(inputDirectory, assemblyName + ".dll"));
            File.Copy(Path.Combine(outputDirectory, assemblyName + ".pdb"), Path.Combine(inputDirectory, assemblyName + ".pdb"));
        }

        var dataFlowResult = ReadDataFlows(inputDirectory);

        var sinkAssemblies = dataFlowResult.Nodes
            .Where(node => node is { Kind: "Sink", Name: "Start" })
            .Select(node => node.Properties.GetValueOrDefault("assembly"))
            .Where(assembly => !string.IsNullOrWhiteSpace(assembly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Contains("AssemblyNodeScopeOne.dll", sinkAssemblies);
        Assert.Contains("AssemblyNodeScopeTwo.dll", sinkAssemblies);
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyMethodSummaries_UseDecodedParameterTypesForOverloads()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblySummaryOverloads", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        var command = Choose(args[0]);
        Process.Start(command);
    }

    private static string Choose(string value) => value;

    private static int Choose(int value) => value;
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblySummaryOverloads.dll"));

        Assert.Contains(dataFlowResult.MethodSummaries, summary => summary.Method.Contains("Program.Choose(string):string", StringComparison.Ordinal));
        Assert.Contains(dataFlowResult.MethodSummaries, summary => summary.Method.Contains("Program.Choose(int):int", StringComparison.Ordinal));
        var chooseStringSummary = Assert.Single(dataFlowResult.MethodSummaries, summary => summary.Method == "Program.Choose(string):string");
        Assert.Equal(string.Empty, chooseStringSummary.Identity?.Namespace);
        Assert.Equal("Program", chooseStringSummary.Identity?.ClassName);
        Assert.Equal("Choose", chooseStringSummary.Identity?.MethodName);
        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command", SinkArgument: "arg0" });
    }

    [Fact]
    public void AssemblyAnalysis_PortablePdbLocations_UseCallInstructionOffset()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyPdbOffsetFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        var command = args[0];
        Process.Start(command);
    }
}
""");

        var assemblyPath = Path.Combine(outputDirectory, "AssemblyPdbOffsetFlow.dll");
        var methodsSlice = ReadMethods(assemblyPath);
        var dataFlowResult = ReadDataFlows(assemblyPath);

        Assert.Contains(methodsSlice.MethodCalls!, call =>
            call is { EvidenceKind: AnalysisEvidenceKind.AssemblyIlDirect, CalledMethod: "Start", FileName: "Program.cs", LineNumber: 8 });
        Assert.Contains(dataFlowResult.Nodes, node =>
            node is { Kind: "Sink", Name: "Start", FileName: "Program.cs", LineNumber: 8 });
    }

    [Fact]
    public void GetDataFlows_AssemblyDirectoryWithDepsJson_ScopesToProjectAssemblies()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyScopedFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");
        var runtimeAssembly = typeof(object).Assembly.Location;
        File.Copy(runtimeAssembly, Path.Combine(outputDirectory, Path.GetFileName(runtimeAssembly)), overwrite: true);

        var dataFlowResult = ReadDataFlows(outputDirectory);

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.DoesNotContain(dataFlowResult.Nodes, node => node.Properties.TryGetValue("assembly", out var assembly) && assembly == Path.GetFileName(runtimeAssembly));
    }

    [Fact]
    public void GetDataFlows_AssemblyDirectoryWithMalformedDepsJson_RemainsBestEffort()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyMalformedDepsFlow", """
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");
        File.WriteAllText(Path.Combine(outputDirectory, "AssemblyMalformedDepsFlow.deps.json"), """
{
  "libraries": {
    "AssemblyMalformedDepsFlow/1.0.0": {
      "type": null
    }
  }
}
""");

        var dataFlowResult = ReadDataFlows(outputDirectory);

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyAsyncStateMachine_ReconstructsCapturedArgumentFlow()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyAsyncFlow", """
using System.Diagnostics;
using System.Threading.Tasks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await Task.Yield();
        Process.Start(args[0]);
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyAsyncFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, FileName: "Program.cs", LineNumber: > 1 });
    }

    [Fact]
    public void GetDataFlows_AssemblyOnlyDelegateClosure_ReconstructsCapturedArgumentFlow()
    {
        using var tempDirectory = new TemporaryDirectory();
        var outputDirectory = BuildTemporaryProject(tempDirectory.Path, "AssemblyDelegateFlow", """
using System;
using System.Diagnostics;

public static class Program
{
    public static void Main(string[] args)
    {
        Action launch = () => Process.Start(args[0]);
        launch();
    }
}
""");

        var dataFlowResult = ReadDataFlows(Path.Combine(outputDirectory, "AssemblyDelegateFlow.dll"));

        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, FileName: "Program.cs", LineNumber: > 1 });
    }

    [Fact]
    public void GetDataFlows_NestedInterproceduralExpression_PreservesCommandSlice()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "NestedFlow.cs"), """
using System.Diagnostics;

class NestedFlow
{
    static void Main(string[] args)
    {
        var command = Wrap(string.Concat(args[0], ""));
        Launch(command);
    }

    static string Wrap(string input) => input.Trim();

    static void Launch(string command)
    {
        Process.Start(command);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);
        var nodeIds = result.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("NestedFlow.Launch") && summary.SinkParameterIndexes.Contains(0));
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("NestedFlow.Launch") && summary is { EvidenceKind: AnalysisEvidenceKind.SourceRoslynSummary, Identity: not null });
        Assert.All(result.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void CryptoAnalysis_DetectsWeakCryptoHardcodedMaterialAndCycloneDxExport()
    {
        using var tempDirectory = new TemporaryDirectory();
        var samplePath = Path.Combine(tempDirectory.Path, "CryptoSample.cs");
        File.WriteAllText(samplePath, """
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

class CryptoSample
{
    static string StaticKey = "0123456789abcdef0123456789abcdef";
    static string StaticNonce = "0123456789ab";

    static void Main(string[] args)
    {
        Hash(args[0]);
        Encrypt(args[0]);
        _ = SslProtocols.Ssl3;
    }

    public static byte[] Hash(string input)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(Encoding.UTF8.GetBytes(input));
    }

    public static byte[] Encrypt(string input)
    {
        var key = Encoding.UTF8.GetBytes(StaticKey);
        var nonce = Encoding.UTF8.GetBytes(StaticNonce);
        var plaintext = Encoding.UTF8.GetBytes(input);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return ciphertext;
    }

    public static HttpClient UnsafeClient()
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        return new HttpClient(handler);
    }
}
""");

        var result = CryptoAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Assets, asset => asset is { Name: "MD5", Strength: "weak" });
        Assert.Contains(result.Materials, material => material is { MaterialType: "key-or-secret", Storage: "hardcoded", Fingerprint: not null });
        Assert.Contains(result.Materials, material => material is { MaterialType: "iv-or-nonce", Storage: "hardcoded", Fingerprint: not null });
        Assert.DoesNotContain(result.Materials, material => material is { MaterialType: "iv-or-nonce", Location.LineNumber: 7 });
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-MD5");
        Assert.Contains(result.Findings, finding => finding is { RuleId: "DOSAI-CRYPTO-WEAK-HASH-MD5", ReachableFromEntryPoint: true });
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED");
        // The line-mode TLS detection carries no resolvable method id, so the old whole-file
        // reachability guess is gated off, with a diagnostic naming the file, instead of being
        // asserted as High-confidence reachable in the CBOM.
        Assert.Contains(result.Protocols, protocol => protocol is { Name: "TLS", Version: "SSL 3.0" });
        Assert.All(result.Protocols, protocol => Assert.False(protocol.ReachableFromEntryPoint));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("matched only at file level", StringComparison.Ordinal) && diagnostic.Contains("CryptoSample.cs", StringComparison.Ordinal));
        Assert.Equal(result.Findings.Select(finding => (finding.RuleId, finding.Location.FileName, finding.Location.LineNumber)).Distinct().Count(), result.Findings.Count);
        Assert.NotNull(result.CryptoDataFlows);
        Assert.True(result.Statistics.CryptoDataFlowSliceCount >= 1);
        Assert.Contains(result.Materials, material => material.DataFlowSliceIds.Count > 0);
        Assert.Contains(result.Operations, operation => operation.DataFlowSliceIds.Count > 0);
        Assert.Contains(result.Findings, finding => finding.DataFlowSliceIds.Count > 0 && (finding.SourceMaterialIds.Count > 0 || finding.SinkOperationIds.Count > 0));

        var cdx = CryptoAnalyzer.GetCryptoAnalysis(tempDirectory.Path, "cyclonedx");
        using var document = JsonDocument.Parse(cdx);
        Assert.Equal("CycloneDX", document.RootElement.GetProperty("bomFormat").GetString());
        Assert.Equal("http://cyclonedx.org/schema/bom-1.6.schema.json", document.RootElement.GetProperty("$schema").GetString());
        var components = document.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.True(components.Count >= 1);
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:evidenceType", "asset"));
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:evidenceType", "operation"));
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:evidenceType", "material"));
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:dataFlowSliceIds"));
        Assert.Contains(document.RootElement.GetProperty("vulnerabilities").EnumerateArray(), vulnerability => HasProperty(vulnerability, "dosai:crypto:dataFlowSliceIds"));
        Assert.True(document.RootElement.GetProperty("dependencies").GetArrayLength() >= 1);

        // Schema validity: components and vulnerabilities reference themselves with the
        // hyphenated bom-ref key, cryptographic-asset components carry
        // cryptoProperties.assetType from the CycloneDX enum, and every graph reference
        // (affects, dependencies) resolves to an emitted component.
        var bomRefs = new HashSet<string>();
        foreach (var component in components)
        {
            Assert.True(component.TryGetProperty("bom-ref", out var bomRef));
            Assert.False(component.TryGetProperty("bomRef", out _));
            bomRefs.Add(bomRef.GetString()!);
        }
        var cryptoAssetComponents = components.Where(component => component.GetProperty("type").GetString() == "cryptographic-asset").ToList();
        Assert.Contains(cryptoAssetComponents, component => component.GetProperty("cryptoProperties").GetProperty("assetType").GetString() == "algorithm");
        foreach (var cryptoAssetComponent in cryptoAssetComponents)
        {
            var assetType = cryptoAssetComponent.GetProperty("cryptoProperties").GetProperty("assetType").GetString();
            Assert.Contains(assetType, new[] { "algorithm", "certificate", "protocol", "related-crypto-material" });
        }
        // MD5 and TLS appear once per detected location, so assert on matching members
        // instead of Single: the cryptoProperties mapping must hold for every copy.
        Assert.Contains(cryptoAssetComponents, component =>
            component.GetProperty("name").GetString() == "MD5" &&
            component.GetProperty("cryptoProperties").GetProperty("assetType").GetString() == "algorithm" &&
            component.GetProperty("cryptoProperties").GetProperty("algorithmProperties").GetProperty("primitive").GetString() == "hash");
        Assert.Contains(cryptoAssetComponents, component =>
            component.GetProperty("name").GetString() == "TLS" &&
            component.GetProperty("cryptoProperties").GetProperty("assetType").GetString() == "protocol" &&
            component.GetProperty("cryptoProperties").GetProperty("protocolProperties").GetProperty("type").GetString() == "tls" &&
            component.GetProperty("cryptoProperties").GetProperty("protocolProperties").GetProperty("version").GetString() == "SSL 3.0");
        foreach (var vulnerability in document.RootElement.GetProperty("vulnerabilities").EnumerateArray())
        {
            Assert.True(vulnerability.TryGetProperty("bom-ref", out _));
            Assert.False(vulnerability.TryGetProperty("bomRef", out _));
            foreach (var affected in vulnerability.GetProperty("affects").EnumerateArray())
            {
                Assert.Contains(affected.GetProperty("ref").GetString()!, bomRefs);
            }
        }
        foreach (var dependency in document.RootElement.GetProperty("dependencies").EnumerateArray())
        {
            Assert.Contains(dependency.GetProperty("ref").GetString()!, bomRefs);
            if (dependency.TryGetProperty("dependsOn", out var dependsOn))
            {
                foreach (var target in dependsOn.EnumerateArray())
                {
                    Assert.Contains(target.GetString()!, bomRefs);
                }
            }
        }

        static bool HasProperty(JsonElement component, string name, string? value = null)
        {
            return component.TryGetProperty("properties", out var properties)
                   && properties.EnumerateArray().Any(property => property.GetProperty("name").GetString() == name && (value is null || property.GetProperty("value").GetString() == value));
        }
    }

    [Fact]
    public void CryptoAnalysis_DetectsNativeTlsSymbolsWithUnderscoreAndVersionPrefixes()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "native_tls.cpp"), """
#include <openssl/ssl.h>

void configure_tls()
{
    auto default_method = TLS_method();
    auto tls12_method = TLSv1_2_method();
    auto tls12_alias = TLS1_2_method();
}
""");

        var result = CryptoAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Protocols, protocol => protocol is { Name: "TLS", Symbol: "TLS_method" });
        Assert.Contains(result.Protocols, protocol => protocol is { Name: "TLS", Version: "TLS 1.2", Symbol: "TLSv1_2_method" });
        Assert.Contains(result.Protocols, protocol => protocol is { Name: "TLS", Version: "TLS 1.2", Symbol: "TLS1_2_method" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "TLS", Symbol: "TLS_method" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "TLS", Symbol: "TLSv1_2_method" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "TLS", Symbol: "TLS1_2_method" });
    }

    // .NET 11 crypto additions: the Aes key-wrap methods (RFC 3394) are real crypto operations
    // that belong in the CBOM, and the experimental caller-driven TLS session types in
    // System.Net.Security carry diagnostic SYSLIB5007, which analysts should see flagged.
    [Fact]
    public void CryptoAnalysis_DetectsNet11KeyWrapAndTlsSessionApis()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Net11KeyWrapSample.cs"), """
using System.Security.Cryptography;

class Net11KeyWrapSample
{
    static byte[] WrapPayload(byte[] key, byte[] payload)
    {
        using var aes = Aes.Create();
        return aes.EncryptKeyWrap(key, payload);
    }

    static byte[] UnwrapPayload(byte[] key, byte[] wrapped)
    {
        using var aes = Aes.Create();
        return aes.DecryptKeyWrap(key, wrapped);
    }
}
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Net11TlsSessionSample.cs"), """
using System.Net.Security;

class Net11TlsSessionSample
{
    static void Exchange()
    {
        var session = new TlsBufferSession();
        session.Handshake();
    }
}
""");

        var result = CryptoAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Assets, asset => asset is { Name: "AES Key Wrap", Family: "key-wrap", Strength: "strong", Standard: "RFC 3394" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "AES Key Wrap", OperationType: "key-wrap/unwrap" });
        Assert.Contains(result.Assets, asset => asset is { Name: "TLS", Family: "protocol" });
        Assert.Contains(result.Findings, finding => finding is { RuleId: "DOSAI-CRYPTO-EXPERIMENTAL-TLS-API", Severity: "Low" });
    }

    // .NET 11's X25519 Diffie-Hellman (RFC 7748) and the padded AES Key Wrap variant (RFC 5649)
    // extend the managed crypto surface: both are strong primitives that belong in the CBOM, and
    // the padded names must not fall through to the generic Aes classification.
    [Fact]
    public void CryptoAnalysis_DetectsX25519AndPaddedKeyWrap()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Net11X25519Sample.cs"), """
using System.Security.Cryptography;

class Net11X25519Sample
{
    static byte[] Agree()
    {
        using X25519DiffieHellman alice = X25519DiffieHellman.GenerateKey();
        using X25519DiffieHellman bob = X25519DiffieHellman.GenerateKey();
        return alice.DeriveRawSecretAgreement(bob);
    }
}
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Net11KeyWrapPaddedSample.cs"), """
using System.Security.Cryptography;

class Net11KeyWrapPaddedSample
{
    static byte[] Wrap(byte[] key)
    {
        using var aes = Aes.Create();
        return aes.EncryptKeyWrapPadded(key);
    }

    static byte[] Unwrap(byte[] wrapped)
    {
        using var aes = Aes.Create();
        return aes.DecryptKeyWrapPadded(wrapped);
    }
}
""");

        var result = CryptoAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Assets, asset => asset is { Name: "X25519", Family: "key-agreement", Strength: "strong", Standard: "RFC 7748" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "X25519", OperationType: "key-agreement" });
        Assert.Contains(result.Assets, asset => asset is { Name: "AES Key Wrap", Strength: "strong", Standard: "RFC 5649" });
    }

    // .NET's post-quantum algorithms (FIPS 203/204/205) are part of the modern .NET baseline;
    // the CBOM must classify them as strong key-agreement/signature primitives, not drop them.
    [Fact]
    public void CryptoAnalysis_DetectsPostQuantumAlgorithms()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "PqcSample.cs"), """
using System.Security.Cryptography;

class PqcSample
{
    static byte[] Sign(byte[] data)
    {
        using var mldsa = MLDsa.ImportFromPem(File.ReadAllText("ml-dsa.pem"));
        return mldsa.SignData(data);
    }

    static void Encapsulate()
    {
        using var mlkem = MLKem.GenerateKey();
        mlkem.Encapsulate(Span<byte>.Empty, Span<byte>.Empty);
    }

    static bool Verify(byte[] data, byte[] signature)
    {
        using var slhdsa = SlhDsa.ImportFromPem(File.ReadAllText("slh-dsa.pem"));
        return slhdsa.VerifyData(data, signature);
    }
}
""");

        var result = CryptoAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Assets, asset => asset is { Name: "ML-DSA", Family: "signature", Strength: "strong", Standard: "FIPS 204" });
        Assert.Contains(result.Assets, asset => asset is { Name: "ML-KEM", Family: "key-agreement", Strength: "strong", Standard: "FIPS 203" });
        Assert.Contains(result.Assets, asset => asset is { Name: "SLH-DSA", Family: "signature", Strength: "strong", Standard: "FIPS 205" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "ML-DSA", OperationType: "sign" });
        Assert.Contains(result.Operations, operation => operation is { Algorithm: "ML-KEM", OperationType: "key-agreement/encapsulate" });
    }

    [Fact]
    public void GetMethods_FSharpRAndVcxxSources_ReturnsFrontendMethodsCallsAndValidCallGraph()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "App.fs"), """
module Sample.App

let run value =
    printfn "%s" value
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "app.R"), """
library(DBI)
run <- function(input) {
  system(input$cmd)
  DBI::dbGetQuery(con, input$sql)
}
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "native.cpp"), """
#include <cstdlib>
int main(int argc, char** argv) {
  system(argv[1]);
  return 0;
}
""");

        var result = Depscan.Dosai.GetMethods(tempDirectory.Path);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

        Assert.NotNull(methodsSlice);
        Assert.Contains(methodsSlice.Methods ?? [], method => method is { Module: "LanguageFrontend", Name: "run" });
        Assert.Contains(methodsSlice.Methods ?? [], method => method is { Name: "run", Namespace: "R", Module: "R.NativeParser" or "LanguageFrontend" });
        if (LanguageFrontendAnalyzer.IsRNativeParserAvailable)
        {
            Assert.Contains(methodsSlice.Methods ?? [], method => method is { Name: "run", Module: "R.NativeParser" });
            Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { Name: "DBI", Module: "R.NativeParser" });
        }
        Assert.Contains(methodsSlice.Methods ?? [], method => method is { Module: "VC++", Name: "main" });
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod == "system");
        Assert.DoesNotContain(methodsSlice.MethodCalls ?? [], call => call.CalledMethod == "main");
        Assert.NotNull(methodsSlice.CallGraph);
        var nodeIds = methodsSlice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(methodsSlice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void GetMethods_CSharpTopLevelStatements_CapturesMethodCallsInCallGraph()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), """
using System;

Console.WriteLine("hello");
""");

        var result = Depscan.Dosai.GetMethods(tempDirectory.Path);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice);
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod is not null && call.CalledMethod.Contains("System.Console.WriteLine") && call.TargetId is not null && call.TargetId.Contains("System.Console.WriteLine"));
        Assert.Contains(methodsSlice.CallGraph?.Edges ?? [], edge => edge.TargetId.Contains("System.Console.WriteLine"));
    }

    [Fact]
    public void GetMethods_RNativeParserTimeout_FallsBackToRegexFrontend()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var toolDirectory = Path.Combine(tempDirectory.Path, "tools");
        Directory.CreateDirectory(toolDirectory);
        var fakeRscript = Path.Combine(toolDirectory, "Rscript");
        File.WriteAllText(fakeRscript, "#!/bin/sh\nsleep 5\n");
        File.SetUnixFileMode(fakeRscript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(tempDirectory.Path, "app.R"), """
run <- function(input) {
  print(input)
}
""");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalTimeout = Environment.GetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS");
        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDirectory + Path.PathSeparator + originalPath);
            Environment.SetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS", "100");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var (methods, _, _) = LanguageFrontendAnalyzer.GetMethods(tempDirectory.Path);

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"R parser timeout should be enforced before fallback, but took {stopwatch.Elapsed}.");
            Assert.Contains(methods, method => method is { Name: "run", Module: "LanguageFrontend" });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS", originalTimeout);
        }
    }

    [Fact]
    public void GetDataFlows_RAndVcxxSources_ReturnsFrontendSlicesWithValidEdges()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "app.R"), """
run <- function(input) {
  cmd <- input$cmd
  system(cmd)
}
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "native.cpp"), """
#include <cstdlib>
int main(int argc, char** argv) {
  auto cmd = argv[1];
  system(cmd);
  return 0;
}
""");

        var resultJson = DataFlowAnalyzer.GetDataFlows(tempDirectory.Path);
        var result = JsonSerializer.Deserialize<DataFlowResult>(resultJson, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

        Assert.NotNull(result);
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "command", Confidence: "Low" });
        Assert.Contains(result.Nodes, node => node.Properties.TryGetValue("analysis", out var analysis) && analysis == "language-frontend");
        var nodeIds = result.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [Fact]
    public void GetDataFlows_CustomPatterns_MergeWithDefaultsAndFindSlice()
    {
        using var tempDirectory = new TemporaryDirectory();
        var samplePath = Path.Combine(tempDirectory.Path, "CustomFlow.cs");
        File.WriteAllText(samplePath, """
class Input
{
    public static string Get() => "tainted";
}

class Dangerous
{
    public static void Exec(string value) { }
}

class CustomFlow
{
    static void Run()
    {
        var value = Input.Get();
        Dangerous.Exec(value);
    }
}
""");
        var patternsPath = Path.Combine(tempDirectory.Path, "patterns.json");
        File.WriteAllText(patternsPath, """
{
  "sources": [
    { "kind": "Method", "match": "Contains", "pattern": "Input.Get", "category": "custom-source" }
  ],
  "sinks": [
    { "kind": "Method", "match": "Contains", "pattern": "Dangerous.Exec", "category": "custom-sink" }
  ]
}
""");

        var resultJson = DataFlowAnalyzer.GetDataFlows(tempDirectory.Path, patternsPath);
        var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(dataFlowResult);
        Assert.Contains(dataFlowResult.Patterns.Sources, pattern => pattern.Pattern == "Input.Get");
        Assert.Contains(dataFlowResult.Patterns.Sinks, pattern => pattern.Pattern == "Dangerous.Exec");
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSource: true, Category: "custom-source" });
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, Category: "custom-sink" });
        Assert.Contains(dataFlowResult.Slices, slice => slice is { SourceCategory: "custom-source", SinkCategory: "custom-sink" });
    }

    [Fact]
    public void GetDataFlows_CustomPatterns_CanAttachPurlsToSourcesAndSinks()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "PurlFlow.cs"), """
class Input { public static string Get() => "tainted"; }
class Dangerous { public static void Exec(string value) { } }
class PurlFlow
{
    static void Run()
    {
        var value = Input.Get();
        Dangerous.Exec(value);
    }
}
""");
        var patternsPath = Path.Combine(tempDirectory.Path, "patterns.json");
        File.WriteAllText(patternsPath, """
{
  "sources": [ { "kind": "Method", "pattern": "Input.Get", "category": "custom-source", "purl": "pkg:nuget/Input.Package@1.0.0" } ],
  "sinks": [ { "kind": "Method", "pattern": "Dangerous.Exec", "category": "custom-sink", "purl": "pkg:nuget/Dangerous.Package@2.0.0" } ]
}
""");

        var result = JsonSerializer.Deserialize<DataFlowResult>(DataFlowAnalyzer.GetDataFlows(tempDirectory.Path, patternsPath), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(result);
        Assert.Contains(result.Nodes, node => node is { IsSource: true, Purl: "pkg:nuget/Input.Package@1.0.0" });
        Assert.Contains(result.Nodes, node => node is { IsSink: true, Purl: "pkg:nuget/Dangerous.Package@2.0.0" });
        Assert.Contains(result.Slices, slice => slice.Purls.Contains("pkg:nuget/Input.Package@1.0.0") && slice.Purls.Contains("pkg:nuget/Dangerous.Package@2.0.0"));
    }

    [Fact]
    public void GetDataFlows_CSharpAdvancedEdges_TracksReceiverAndProcessStartInfoFlows()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "AdvancedFlow.cs"), """
using System.Diagnostics;

class Upload { public FileLike File { get; set; } = new(); }
class FileLike { public void CopyTo(object stream) { } }

class AdvancedFlow
{
    static void Main(string[] args)
    {
        var psi = new ProcessStartInfo($"/bin/{args[0]}");
        var upload = new Upload();
        upload.File.CopyTo(new object());
    }

    static void Save(Upload model)
    {
        model.File.CopyTo(new object());
    }
}
""");

        var result = JsonSerializer.Deserialize<DataFlowResult>(DataFlowAnalyzer.GetDataFlows(tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(result);
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "command");
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "file", SinkArgumentIndex: -1 });
    }

    [Fact]
    public void GetDataFlows_SanitizerAndValidator_StopFlowsToSink()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "SanitizedFlow.cs"), """
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

class SanitizedFlow
{
    static void Main(string[] args)
    {
        var encoded = WebUtility.HtmlEncode(args[0]);
        Process.Start(encoded);

        var guarded = args[1];
        if (Regex.IsMatch(guarded, "^[a-z]+$"))
        {
            Process.Start(guarded);
        }
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.Patterns.Sanitizers, pattern => pattern.Category == "html-encoding");
        Assert.Contains(result.Patterns.Sanitizers, pattern => pattern.Category == "validation");
        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "command");
    }

    [Fact]
    public void GetDataFlows_FieldSensitiveObjectTaint_DoesNotTaintSiblingInstance()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "FieldFlow.cs"), """
using System.Diagnostics;

class Box { public string? Value { get; set; } }
class FieldFlow
{
    static void Main(string[] args)
    {
        var tainted = new Box();
        var clean = new Box();
        tainted.Value = args[0];
        Process.Start(clean.Value);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "command");
    }

    [Fact]
    public void GetDataFlows_InterproceduralSummary_ReplaysCalleeSinkAtCallSite()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "SummaryFlow.cs"), """
using System.Diagnostics;

class SummaryFlow
{
    static void Main(string[] args)
    {
        Run(args[0]);
    }

    static void Run(string command)
    {
        Process.Start(command);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("SummaryFlow.Run") && summary.SinkParameterIndexes.Contains(0));
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "command" && result.Nodes.Any(node => node.Id == slice.SinkId && node.Kind == "Sink" && node.Properties.ContainsKey("summaryMethod")));
    }

    [Fact]
    public void GetDataFlows_PatternPacks_CanSelectAdditionalFrameworkPatterns()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "DapperFlow.cs"), """
namespace Dapper { public static class SqlMapper { public static void Execute(object connection, string sql) { } } }
class DapperFlow
{
    static void Main(string[] args)
    {
        Dapper.SqlMapper.Execute(new object(), args[0]);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path, patternPacks: "data");

        Assert.Contains(result.Patterns.PatternPacks, pack => pack == "data");
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "sql");
    }

    [Fact]
    public void GetMethods_CSharpSource_CapturesRicherAuthorizationMetadata()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "AuthEndpoints.cs"), """
using System;

class RouteAttribute : Attribute { public RouteAttribute(string value) { } }
class HttpPostAttribute : Attribute { public HttpPostAttribute(string value) { } }
class AuthorizeAttribute : Attribute { public AuthorizeAttribute() { } public AuthorizeAttribute(string policy) { } public string? Policy { get; set; } public string? Roles { get; set; } public string? AuthenticationSchemes { get; set; } }
class RequiredScopeAttribute : Attribute { public RequiredScopeAttribute(string scope) { } }
class EnableCorsAttribute : Attribute { public EnableCorsAttribute(string policy) { } }
class ValidateAntiForgeryTokenAttribute : Attribute { }

[Authorize("OrdersPolicy", Roles = "Admin,Auditor", AuthenticationSchemes = "Bearer")]
[Route("api/orders")]
class OrdersController
{
    [HttpPost("{id}")]
    [RequiredScope("orders.write")]
    [EnableCors("Internal")]
    [ValidateAntiForgeryToken]
    public string Update(string id) => id;
}
""");

        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var endpoint = Assert.Single(methodsSlice?.ApiEndpoints ?? [], endpoint => endpoint.Route == "api/orders/{id}");
        Assert.True(endpoint.AuthorizationRequired);
        Assert.Contains("OrdersPolicy", endpoint.AuthorizationPolicies);
        Assert.Contains("Admin", endpoint.Roles);
        Assert.Contains("Auditor", endpoint.Roles);
        Assert.Contains("Bearer", endpoint.AuthenticationSchemes);
        Assert.Contains("orders.write", endpoint.RequiredScopes);
        Assert.Contains("Internal", endpoint.CorsPolicies);
        Assert.True(endpoint.AntiForgeryRequired);
        // Schema 4.0.0: entry-point Route carries the resolved path, not the verbatim template.
        Assert.Contains(methodsSlice?.EntryPoints ?? [], entryPoint => entryPoint is { Route: "/api/orders/{id}", AuthorizationRequired: true } && entryPoint.Roles.Contains("Admin"));
    }

    [Fact]
    public void QueryEngine_FiltersDataFlowJsonAndMcpListsTools()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "QueryFlow.cs"), """
using System.Diagnostics;
class QueryFlow { static void Main(string[] args) { Process.Start(args[0]); } }
""");

        var json = DataFlowAnalyzer.GetDataFlows(tempDirectory.Path);
        var queryResult = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(json, "slices[sinkCategory=command]"));
        Assert.NotNull(queryResult);
        Assert.NotEmpty(queryResult);

        var numericQueryResult = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson("""
{
  "items": [
    { "count": 9 },
    { "count": 10 },
    { "count": 11 }
  ]
}
""", "items[count>=10 && count<=11]"));
        Assert.NotNull(numericQueryResult);
        Assert.Equal(2, numericQueryResult.Count);

        using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n");
        using var output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, input, output);
        Assert.Contains("dosai.dataflows", output.ToString());
    }

    [Fact]
    public void CryptoWorkflow_CliFormatsQueryAliasesAndMcpTool_ReturnExpectedEvidence()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "CryptoWorkflow.cs"), """
using System.Security.Cryptography;
using System.Text;

class CryptoWorkflow
{
    static void Main(string[] args) => Hash(args[0]);
    static byte[] Hash(string value)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(Encoding.UTF8.GetBytes(value));
    }
}
""");

        var nativeOutput = Path.Combine(tempDirectory.Path, "crypto.json");
        var cdxOutput = Path.Combine(tempDirectory.Path, "cbom.json");
        var unsupportedFormatOutput = Path.Combine(tempDirectory.Path, "unsupported.json");

        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", nativeOutput, "--format", "dosai"]));
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", cdxOutput, "--format", "cyclonedx", "--graph-format", "graphml,gexf"]));
        Assert.Equal(1, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", unsupportedFormatOutput, "--format", "unsupported"]));
        Assert.True(File.Exists(Path.Combine(tempDirectory.Path, "cbom-dataflows.graphml")));
        Assert.True(File.Exists(Path.Combine(tempDirectory.Path, "cbom-dataflows.gexf")));

        using var nativeDocument = JsonDocument.Parse(File.ReadAllText(nativeOutput));
        Assert.True(nativeDocument.RootElement.GetProperty("Statistics").GetProperty("ReachableFindingCount").GetInt32() >= 1);
        Assert.True(nativeDocument.RootElement.GetProperty("Statistics").GetProperty("CryptoDataFlowSliceCount").GetInt32() >= 1);
        Assert.True(nativeDocument.RootElement.TryGetProperty("CryptoDataFlows", out _));

        var semanticAnalysis = CryptoAnalyzer.Analyze(tempDirectory.Path);
        Assert.Contains(semanticAnalysis.Operations, operation => operation.Algorithm == "MD5" && operation.Properties.TryGetValue("source", out var source) && source == "roslyn");

        var findingQuery = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(File.ReadAllText(nativeOutput), "findings[ruleId~=MD5]"));
        var assetQuery = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(File.ReadAllText(nativeOutput), "assets[family=hash]"));
        Assert.NotNull(findingQuery);
        Assert.NotEmpty(findingQuery);
        Assert.NotNull(assetQuery);
        Assert.NotEmpty(assetQuery);

        using var cdxDocument = JsonDocument.Parse(File.ReadAllText(cdxOutput));
        Assert.Equal("CycloneDX", cdxDocument.RootElement.GetProperty("bomFormat").GetString());
        var combinedComponents = cdxDocument.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.True(combinedComponents.Count >= 1);
        Assert.Contains(combinedComponents, component => HasProperty(component, "dosai:crypto:evidenceType", "asset"));
        Assert.Contains(combinedComponents, component => HasProperty(component, "dosai:crypto:evidenceType", "operation"));
        Assert.True(cdxDocument.RootElement.GetProperty("vulnerabilities").GetArrayLength() >= 1);
        Assert.Contains(cdxDocument.RootElement.GetProperty("components").EnumerateArray(), component => HasProperty(component, "dosai:crypto:dataFlowSliceIds"));
        Assert.Contains(cdxDocument.RootElement.GetProperty("vulnerabilities").EnumerateArray(), vulnerability => HasProperty(vulnerability, "dosai:crypto:dataFlowSliceIds"));

        using var input = new StringReader("""
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.crypto","arguments":{"format":"cyclonedx"}}}

""");
        using var output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, input, output);
        Assert.Contains("CycloneDX", output.ToString());
        Assert.Contains("dosai:crypto:reachableFromEntryPoint", output.ToString());

        static bool HasProperty(JsonElement component, string name, string? value = null)
        {
            return component.TryGetProperty("properties", out var properties)
                   && properties.EnumerateArray().Any(property => property.GetProperty("name").GetString() == name && (value is null || property.GetProperty("value").GetString() == value));
        }
    }

    [Fact]
    public void GetDataFlows_VisualBasic_CliSourceToProcessStart_ReturnsSlice()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "VbFlow.vb"), """
Imports System.Diagnostics

Module VbFlow
    Sub Main(args As String())
        Dim command = args(0)
        Process.Start(command)
    End Sub
End Module
""");

        var result = JsonSerializer.Deserialize<DataFlowResult>(DataFlowAnalyzer.GetDataFlows(tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(result);
        Assert.Contains(result.Nodes, node => node is { IsSource: true, Category: "cli", FileName: "VbFlow.vb" });
        Assert.Contains(result.Nodes, node => node is { IsSink: true, Category: "command", FileName: "VbFlow.vb" });
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    [Fact]
    public void GetMethods_CSharpSource_CapturesApiEndpointsAndUrls()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Endpoints.cs"), """
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.AspNetCore.Mvc
{
    public class RouteAttribute : System.Attribute { public RouteAttribute(string value) { } }
    public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string value) { } }
}

[Route("api/[controller]")]
class OrdersController
{
    [HttpGet("{id}")]
    public string Get(string id) => "https://api.example.test/orders/" + id;
}

class Program
{
    void Map(dynamic app)
    {
        app.MapPost("/upload", () => "ok");
    }
}
""");

        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice);
        // Schema 4.0.0: Route keeps the verbatim template while Path carries the resolved,
        // normalized route (tokens substituted, constraints stripped) for CycloneDX consumers.
        // This assertion previously locked in the %5Bcontroller%5D bug (cdxgen discussion #4333)
        // by expecting the unresolved template as the only route value.
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { HttpMethod: "GET", Route: "api/[controller]/{id}", Path: "/api/Orders/{id}", FilePath: "Endpoints.cs" } && endpoint.RawUrls.Contains("https://api.example.test/orders/"));
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { HttpMethod: "POST", Route: "/upload", Path: "/upload", EndpointKind: "MinimalApi" });
        Assert.NotNull(methodsSlice.Metadata);
        Assert.Equal("5.1.0", methodsSlice.Metadata.SchemaVersion);
        Assert.Contains(methodsSlice.EntryPoints ?? [], entryPoint => entryPoint is { Kind: "HttpController", Route: "/api/Orders/{id}" });
    }

    /// <summary>
    ///     Regression test for cdxgen discussion #4333: [Route("[controller]")] on
    ///     WeatherForecastController must yield Path = "/api/WeatherForecast", not the unresolved
    ///     template that cdxgen percent-encoded into %5Bcontroller%5D.
    /// </summary>
    [Fact]
    public void GetMethods_ControllerRouteToken_ResolvesToConcretePath()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Weather.cs"), """
namespace Api;

public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) { } }

[Route("api/[controller]")]
public class WeatherForecastController
{
    [HttpGet("{id:int}")]
    public object Get(int id) => null;
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        var endpoint = Assert.Single(methodsSlice.ApiEndpoints ?? [], e => e.MethodName == "Get");
        Assert.Equal("api/[controller]/{id:int}", endpoint.Route);
        Assert.Equal("/api/WeatherForecast/{id}", endpoint.Path);
        var parameter = Assert.Single(endpoint.RouteParameters);
        Assert.Equal("id", parameter.Name);
        Assert.Equal(["int"], parameter.Constraints);
        Assert.Equal("medium", endpoint.Confidence);
    }

    [Fact]
    public void GetMethods_AbsoluteMethodRoute_OverridesClassRoute()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Override.cs"), """
namespace Api;

public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) { } }

[Route("api/[controller]")]
public class OrdersController
{
    [HttpGet("/admin/orders/{id}")]
    public object Get(int id) => null;
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { Path: "/admin/orders/{id}", Route: "/admin/orders/{id}" });
    }

    [Fact]
    public void GetMethods_ActionNameAndAreaTokens_Resolve()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Tokens.cs"), """
namespace MyApp.Areas.Admin.Controllers;

public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) { } }
public class AreaAttribute : System.Attribute { public AreaAttribute(string name) { } }
public class ActionNameAttribute : System.Attribute { public ActionNameAttribute(string name) { } }

[Area("admin")]
[Route("[area]/[controller]/[action]")]
public class UsersController
{
    [ActionName("detail")]
    [HttpGet("{id?}")]
    public object GetAsync(int id) => null;
}
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { Path: "/admin/Users/detail/{id}" });
    }

    [Fact]
    public void GetMethods_DetectsFrameworks_FromUsingDirectives()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), """
using MassTransit;

class Program { static void Main() { } }
""");

        var methodsSlice = ReadMethods(tempDirectory.Path);

        Assert.Contains(methodsSlice.Frameworks ?? [], framework => framework is { Id: "messaging", DetectionKind: "using", Confidence: "medium" });
    }

    [Fact]
    public void GetDataFlows_OutputIncludesWeaknessCandidatesReachabilityAndAgentContext()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "WeaknessFlow.cs"), """
using System.Diagnostics;

class WeaknessFlow
{
    static void Main(string[] args)
    {
        var command = args[0];
        Process.Start(command);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        Assert.NotNull(result.Metadata);
        Assert.Contains(result.EntryPoints, entryPoint => entryPoint is { Kind: "Cli", MethodName: "Main" });
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "CommandInjectionCandidate", Cwe: "CWE-78" });
        Assert.Contains(result.DangerousApiReachability, api => api.Category == "command");
        var processReachability = Assert.Single(result.PackageReachability, package => package.Purl == "pkg:nuget/System.Diagnostics.Process");
        Assert.Contains(processReachability.SourceLocations, location =>
            location.FileName == "WeaknessFlow.cs" &&
            location.LineNumber > 0 &&
            (location.Kind == "DataFlowNode" || location.Kind == "DataFlowEdge" || location.Kind == "DataFlowSlice"));
        Assert.DoesNotContain(processReachability.SourceLocations, location =>
            location.Path?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true);


        var context = TransparencyBuilder.BuildAgentContext(result, tempDirectory.Path);
        Assert.Contains(context.HighRiskWeaknesses, weakness => weakness.Kind == "CommandInjectionCandidate");
        Assert.Contains("Dosai found", context.Summary);

        var markdown = TransparencyBuilder.ToMarkdownReport(result);
        Assert.Contains("CommandInjectionCandidate", markdown);
        Assert.Contains("WeaknessFlow.cs", markdown);
    }

    [Fact]
    public void BuildPackageReachability_DataFlowSliceLocationsAreScopedToMatchingPurl()
    {
        var result = new DataFlowResult
        {
            Nodes =
            [
                new DataFlowNode
                {
                    Id = "source",
                    Kind = "Source",
                    Name = "args[0]",
                    Path = "Program.cs",
                    FileName = "Program.cs",
                    LineNumber = 5,
                    ColumnNumber = 21,
                    IsSource = true,
                    Category = "cli"
                },
                new DataFlowNode
                {
                    Id = "processSink",
                    Kind = "Sink",
                    Name = "Process.Start",
                    Purl = "pkg:nuget/System.Diagnostics.Process",
                    Path = "Program.cs",
                    FileName = "Program.cs",
                    LineNumber = 10,
                    ColumnNumber = 9,
                    IsSink = true,
                    Category = "command"
                },
                new DataFlowNode
                {
                    Id = "otherPackage",
                    Kind = "Call",
                    Name = "Other.Call",
                    Purl = "pkg:nuget/Other.Package",
                    Path = "Other.cs",
                    FileName = "Other.cs",
                    LineNumber = 20,
                    ColumnNumber = 13
                }
            ],
            Edges =
            [
                new DataFlowEdge
                {
                    Id = "edge-to-process",
                    SourceId = "source",
                    TargetId = "processSink",
                    Kind = "ValueFlow",
                    TargetPurl = "pkg:nuget/System.Diagnostics.Process",
                    Path = "src/Program.cs",
                    FileName = "Program.cs",
                    LineNumber = 10,
                    ColumnNumber = 9
                }
            ],
            Slices =
            [
                new DataFlowSlice
                {
                    Id = "slice1",
                    SourceId = "source",
                    SinkId = "processSink",
                    NodeIds = ["source", "processSink", "otherPackage"],
                    EdgeIds = ["edge-to-process"],
                    SinkCategory = "command",
                    SinkPurl = "pkg:nuget/System.Diagnostics.Process",
                    Purls = ["pkg:nuget/System.Diagnostics.Process", "pkg:nuget/Other.Package"],
                    Confidence = "High"
                }
            ]
        };

        var reachability = TransparencyBuilder.BuildPackageReachability(result);

        var processReachability = Assert.Single(reachability, package => package.Purl == "pkg:nuget/System.Diagnostics.Process");
        Assert.Contains(processReachability.SourceLocations, location => location is { FileName: "Program.cs", LineNumber: 10 });
        Assert.Contains(processReachability.SourceLocations, location => location.Path == "src/Program.cs" && location is { FileName: "Program.cs", LineNumber: 10 });
        Assert.DoesNotContain(processReachability.SourceLocations, location => location is { FileName: "Program.cs", LineNumber: 5 });
        Assert.DoesNotContain(processReachability.SourceLocations, location => location.FileName == "Other.cs");

        var otherReachability = Assert.Single(reachability, package => package.Purl == "pkg:nuget/Other.Package");
        Assert.Contains(otherReachability.SourceLocations, location => location is { FileName: "Other.cs", LineNumber: 20 });
        Assert.DoesNotContain(otherReachability.SourceLocations, location => location is { FileName: "Program.cs", LineNumber: 10 });
    }

    [Fact]
    public void PackageUrlResolver_ProjectAssets_MapsAssembliesAndNamespacesToNuGetPurls()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteProjectAssets(tempDirectory.Path, "Microsoft.Data.SqlClient", "5.1.1", "Microsoft.Data.SqlClient.dll");

        var resolver = PackageUrlResolver.Create(tempDirectory.Path);

        Assert.Equal("pkg:nuget/Microsoft.Data.SqlClient@5.1.1", resolver.Resolve(module: "Microsoft.Data.SqlClient.dll"));
        Assert.Equal("pkg:nuget/Microsoft.Data.SqlClient@5.1.1", resolver.Resolve(symbol: "Microsoft.Data.SqlClient.SqlCommand..ctor(string)"));
    }

    [Fact]
    public void GetMethods_DependencyPurlsPopulatePackageReachabilityForVbFSharpAndR()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteProjectAssets(tempDirectory.Path, "Newtonsoft.Json", "13.0.3", "Newtonsoft.Json.dll");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "DependencyImports.vb"), """
Imports Newtonsoft.Json

Public Module DependencyImports
    Public Sub Run()
    End Sub
End Module
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "DependencyImports.fs"), """
module DependencyImports

open Newtonsoft.Json
open type Newtonsoft.Json.JsonConvert

let run value = value
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "DependencyImports.fsi"), """
module DependencyImports

open Newtonsoft.Json
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "dependencyImports.R"), """
library(Newtonsoft.Json)
run <- function(value) {
  value
}
""");

        var result = Depscan.Dosai.GetMethods(tempDirectory.Path);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice);
        var expectedPurl = "pkg:nuget/Newtonsoft.Json@13.0.3";
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { FileName: "DependencyImports.vb", Purl: var purl } && purl == expectedPurl);
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { FileName: "DependencyImports.fs", Purl: var purl } && purl == expectedPurl);
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { FileName: "DependencyImports.fsi", Purl: var purl } && purl == expectedPurl);
        Assert.DoesNotContain(methodsSlice.Dependencies ?? [], dependency => dependency is { FileName: "DependencyImports.fs", Name: "type" });
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { FileName: "dependencyImports.R", Purl: var purl } && purl == expectedPurl);

        var reachability = Assert.Single(methodsSlice.PackageReachability ?? [], package => package.Purl == expectedPurl);
        Assert.Equal("Low", reachability.Confidence);
        Assert.Contains(reachability.ConfidenceReasons, reason => reason.Contains("dependency/import metadata", StringComparison.Ordinal));
        Assert.Contains(reachability.SourceLocations, location => location is { FileName: "DependencyImports.vb", Kind: "Dependency" });
        Assert.Contains(reachability.SourceLocations, location => location is { FileName: "DependencyImports.fs", Kind: "Dependency" });
        Assert.Contains(reachability.SourceLocations, location => location is { FileName: "DependencyImports.fsi", Kind: "Dependency" });
        Assert.Contains(reachability.SourceLocations, location => location is { FileName: "dependencyImports.R", Kind: "Dependency" });
    }

    [Fact]
    public void PackageUrlResolver_SystemSymbols_MapsCommonFrameworkSymbolsToBestEffortPurls()
    {
        using var tempDirectory = new TemporaryDirectory();
        var resolver = PackageUrlResolver.Create(tempDirectory.Path);

        Assert.Equal("pkg:nuget/System.Diagnostics.Process", resolver.Resolve(symbol: "System.Diagnostics.Process.Start(string)"));
        Assert.Equal("pkg:nuget/System.IO.FileSystem", resolver.Resolve(symbol: "System.IO.File.ReadAllText(string)"));
        Assert.Equal("pkg:nuget/System.Text.Json", resolver.Resolve(symbol: "System.Text.Json.JsonSerializer.Deserialize(string)"));
        Assert.Equal("pkg:nuget/System.Runtime", resolver.Resolve(symbol: "System.Type.GetType(string)"));
    }

    [Fact]
    public void DataFlows_TreeReport_RendersStackTraceStyleFramesWithCode()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "TreeFlow.cs"), """
using System.Diagnostics;

class TreeFlow
{
    static void Main(string[] args)
    {
        var command = args[0];
        Process.Start(command);
    }
}
""");

        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);
        var report = CommandLine.BuildDataFlowTreeReport(result, Path.Combine(tempDirectory.Path, "dataflows.json"));

        Assert.Contains("Dosai Data-flow Analysis", report);
        Assert.Contains("Summary: 1 flow", report);
        Assert.Contains("Data-flow stack traces:", report);
        Assert.Contains("Summary:", report);
        Assert.Contains("Stack (", report);
        Assert.Contains("at Source/cli args", report);
        Assert.Contains("at Assignment command", report);
        Assert.Contains("at Sink/command Start", report);
        Assert.Contains("code: args", report);
        Assert.Contains("code: command", report);
        Assert.Contains("code: Process.Start(command)", report);
        Assert.Contains("TreeFlow.cs:", report);
        Assert.Contains("via VariableAssignment", report);
        Assert.Contains("via SinkArgument", report);
        Assert.Contains("cli → command", report);
        Assert.Contains("pkg:nuget/System.Diagnostics.Process", report);
    }

    [Fact]
    public void DataFlows_Command_PrintsStackTraceOnlyWhenPrintOptionIsPassed()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "PrintFlow.cs"), """
using System.Diagnostics;

class PrintFlow
{
    static void Main(string[] args)
    {
        Process.Start(args[0]);
    }
}
""");

        var outputPath = Path.Combine(tempDirectory.Path, "dataflows.json");
        var printedOutputPath = Path.Combine(tempDirectory.Path, "printed-dataflows.json");

        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            try
            {
                using var quietWriter = new StringWriter();
                Console.SetOut(quietWriter);
                Assert.Equal(0, CommandLine.Main(["dataflows", "--path", tempDirectory.Path, "--o", outputPath]));
                Assert.DoesNotContain("Dosai Data-flow Analysis", quietWriter.ToString());

                using var printWriter = new StringWriter();
                Console.SetOut(printWriter);
                Assert.Equal(0, CommandLine.Main(["dataflows", "--path", tempDirectory.Path, "--o", printedOutputPath, "--print"]));
                var consoleOutput = printWriter.ToString();
                Assert.Contains("Dosai Data-flow Analysis", consoleOutput);
                Assert.Contains("Data-flow stack traces:", consoleOutput);
                Assert.Contains("at Sink/command Start", consoleOutput);
                Assert.Contains("code: Process.Start(args[0])", consoleOutput);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Fact]
    public void GetMethods_ProjectAssets_AddsPurlsToDefaultOutputAndCallGraph()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "FlowSample.cs"), """
using Microsoft.Data.SqlClient;

namespace Microsoft.Data.SqlClient
{
    public class SqlCommand
    {
        public SqlCommand(string sql) { }
    }
}

class FlowSample
{
    static void Main(string[] args)
    {
        var command = new SqlCommand(args[0]);
    }
}
""");
        WriteProjectAssets(tempDirectory.Path, "Microsoft.Data.SqlClient", "5.1.1", "Microsoft.Data.SqlClient.dll");

        var resultJson = Depscan.Dosai.GetMethods(tempDirectory.Path);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(methodsSlice);
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.Purl == "pkg:nuget/Microsoft.Data.SqlClient@5.1.1");
        Assert.Contains(methodsSlice.CallGraph?.Nodes ?? [], node => node.Purl == "pkg:nuget/Microsoft.Data.SqlClient@5.1.1");
        Assert.Contains(methodsSlice.CallGraph?.Edges ?? [], edge => edge.TargetPurl == "pkg:nuget/Microsoft.Data.SqlClient@5.1.1");
    }

    [Fact]
    public void GetDataFlows_ProjectAssets_AddsPurlsToNodesAndSlices()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "SqlFlow.cs"), """
using Microsoft.Data.SqlClient;

namespace Microsoft.Data.SqlClient
{
    public class SqlCommand
    {
        public SqlCommand(string sql) { }
    }
}

class SqlFlow
{
    static void Main(string[] args)
    {
        var sql = args[0];
        var command = new SqlCommand(sql);
    }
}
""");
        WriteProjectAssets(tempDirectory.Path, "Microsoft.Data.SqlClient", "5.1.1", "Microsoft.Data.SqlClient.dll");

        var resultJson = DataFlowAnalyzer.GetDataFlows(tempDirectory.Path);
        var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(dataFlowResult);
        Assert.Contains(dataFlowResult.Nodes, node => node is { IsSink: true, Purl: "pkg:nuget/Microsoft.Data.SqlClient@5.1.1" });
        Assert.Contains(dataFlowResult.Slices, slice => slice.SinkPurl == "pkg:nuget/Microsoft.Data.SqlClient@5.1.1" && slice.Purls.Contains("pkg:nuget/Microsoft.Data.SqlClient@5.1.1"));

        var graphMl = DataFlowExporter.Export(dataFlowResult, DataFlowExportFormat.GraphMl);
        Assert.Contains("pkg:nuget/Microsoft.Data.SqlClient@5.1.1", graphMl);
    }

    [Fact]
    public void GetMethods_VBSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldVBSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(13, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
    
        // Test inheritance and interface implementation for VB.NET
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
    
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
    
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
    
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_FSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldFSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;

        // We expect at least some methods to be detected
        Assert.True(actualMethods?.Count > 0);
        // Check that we have the expected F# functions
        Assert.Contains(actualMethods, m => m.Name == "hello");
        Assert.Contains(actualMethods, m => m.Name == "goodbye");
        Assert.Contains(actualMethods, m => m.Name == "add");
        Assert.Contains(actualMethods, m => m is { ClassName: "Person", Name: "Introduce" });
        Assert.Contains(actualMethods, m => m is { ClassName: "Person", Name: "CelebrateBirthday" });
    }

    // F# 11 ships with .NET 11 (record spreads and constructors, direct delegate construction,
    // interpolated strings). The frontend is line-based and must keep extracting declarations
    // from the new syntax, degrading to reduced coverage rather than failing.
    [Fact]
    public void GetMethods_FSharp11Source_ReturnsFunctionsAndDependencies()
    {
        var sourcePath = GetFilePath(FSharp11FeaturesSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        var actualMethods = methodsSlice?.Methods;

        Assert.True(actualMethods?.Count > 0);
        Assert.Contains(actualMethods, m => m.Name == "describe");
        Assert.Contains(actualMethods, m => m.Name == "relocate");
        Assert.Contains(actualMethods, m => m.Name == "build");
        Assert.Contains(actualMethods, m => m.Name == "register");
    }

    // Module-level `let` bindings that follow a `type` declaration are module members, not type
    // members; the line frontend must reset the class context instead of attributing them to the
    // preceding type.
    [Fact]
    public void GetMethods_FSharp11MoreFeatures_ModuleFunctionsAfterTypesGetModuleClass()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharp11MoreFeaturesSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11More", Name: "origin" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11More", Name: "describe" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11More", Name: "compute" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11More", Name: "summarize" });
    }

    // F# 11 conditional compilation: `#elif` is new, and the analysis define set (modern .NET
    // family defined, DEBUG/TRACE/NETFRAMEWORK undefined) picks the `#else` arm, the
    // `#if !DEBUG` region, and the `NET8_0_OR_GREATER` guard. Multi-target guards are
    // near-universal in real F# libraries; their declarations and sinks must stay visible.
    [Fact]
    public void GetMethods_FSharp11Language_ConditionalDirectivesSelectCompilerBranches()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharp11LanguageSource));

        // `configure` is declared in all three branches but only the `#else` branch is live.
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "configure" });
        // `#if !DEBUG` is satisfied with DEBUG undefined.
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "releaseNotes" });
        // The multi-targeting guard: its declaration (and with it its Process.Start sink line)
        // stays visible - this is the near-universal real-library shape.
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "modernPath" });
        // The legacy framework family stays undefined.
        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { Name: "legacyPath" });

        var calls = methodsSlice.MethodCalls ?? [];
        Assert.Contains(calls, call => call is { CalledMethod: "ignore" } && call.SourceId?.Contains("configure", StringComparison.Ordinal) == true);
        // The inactive DEBUG/TRACE branches' logger calls must not leak in.
        Assert.DoesNotContain(calls, call => call.CalledMethod is "WriteLine" or "Debug" or "Trace");
        // The legacy branch must not leak in either.
        Assert.DoesNotContain(calls, call => call.SourceId?.Contains("legacyPath", StringComparison.Ordinal) == true);
        // `#:`-directive lines are ignored by the F# 11 compiler wherever they appear.
        Assert.DoesNotContain(calls, call => call.CalledMethod is "package" or "property");
        Assert.DoesNotContain(methodsSlice.Dependencies ?? [], dependency => dependency.Name is "package" or "AsyncSeq");
    }

    // Type-level record spreads, anonymous record spreads, and nested dotted updates are all
    // F# 11 shapes the line scanner must not trip on: every function survives as a module member
    // and the spread ellipses produce no phantom calls.
    [Fact]
    public void GetMethods_FSharp11Language_RecordSpreadFormsDeclareFunctions()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharp11LanguageSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "annotate" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "label" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "relocate" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "FSharp11Language", Name: "build" });

        Assert.DoesNotContain(methodsSlice.MethodCalls ?? [], call => call.CalledMethod is "..." or "Config" or "Opts");
    }

    // Comment prose shaped like `word (F# 11)` or declaration keywords (`inherit`, `override`,
    // `abstract`) must not surface as method calls.
    [Fact]
    public void GetMethods_FSharp11MoreFeatures_CommentsAndDeclarationsProduceNoPhantomCalls()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharp11MoreFeaturesSource));

        var calledMethods = (methodsSlice.MethodCalls ?? []).Select(call => call.CalledMethod).ToList();
        Assert.Contains(calledMethods, name => name == "RunSynchronouslyImmediate");
        Assert.Contains(calledMethods, name => name == "Sleep");
        Assert.DoesNotContain(calledMethods, name => name is "inherit" or "override" or "abstract" or "member");
        Assert.DoesNotContain(calledMethods, name => name is "constructors" or " Efficient" or "Efficient" or "interpolated" or "inheritdoc");
    }

    // A `module X =` body is fully indented; module-level `let` bindings that follow a `type`
    // declaration must still be attributed to the module (not the type) at body indentation,
    // and prime identifiers (`x'`, `list'`) must not be read as unterminated char literals.
    [Fact]
    public void GetMethods_FSharpIndentedModule_ModuleLetsAfterTypeGetModuleClass()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharpModuleLayoutsSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Counter", Name: "Next" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "transform" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "eval" });
        // The prime-identifier line is still a module binding (`let x'` extracts as `x`).
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "x" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "list" });
    }

    // The prime identifiers and real char literals coexist: a juxtaposition call on a
    // prime-identifier line (`let x' = transform "seed"`) survives comment stripping, and
    // `dash`/`newline` extract as bindings despite the char literals on their lines.
    [Fact]
    public void GetMethods_FSharpIndentedModule_PrimeIdentifiersDoNotSwallowCalls()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharpModuleLayoutsSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "dash" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "newline" });
        // One juxtaposition call on the `x'` line and one on the `list'` line: both lines are
        // processed to the end (an unterminated `'` would swallow each line's remainder).
        Assert.Equal(2, (methodsSlice.MethodCalls ?? []).Count(call => call.CalledMethod == "transform"));
    }

    // Verbatim (`@"...\"`) and triple-quoted literals span lines and carry their own escape
    // rules; treating `\` as an escape or ending the literal early made the scanner read string
    // body as code, and the declarations after the literal were attributed to whatever the
    // resulting mis-lexed line looked like.
    [Fact]
    public void GetMethods_FSharpMultiLineStringLiterals_AreNotScannedAsCode()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharpModuleLayoutsSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "verbatim" });
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "banner" });
        // The declaration after the multi-line literal is still found, so the literal closed.
        Assert.Contains(methodsSlice.Methods!, method => method is { ClassName: "Indented", Name: "afterLiterals" });
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod == "Write");
        // Text inside the literals is string body, not code.
        var calledMethods = (methodsSlice.MethodCalls ?? []).Select(call => call.CalledMethod).ToList();
        Assert.DoesNotContain(calledMethods, name => name is "notAComment" or "ignored");
    }

    // Modern R (4.1+) assigns lambdas with `name <- \(args) { ... }`; the fallback line parser
    // must treat that as a function declaration and keep comment prose out of the call list.
    [SkippableFact]
    public void GetMethods_ModernRSource_DetectsLambdaAssignedFunctionsAndSkipsComments()
    {
        SkipIfRNativeParserIsInstalled();
        var methodsSlice = ReadMethods(GetFilePath(ModernRFeatureSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { Name: "render", Namespace: "R" });
        Assert.Contains(methodsSlice.Methods!, method => method is { Name: "process", Namespace: "R" });
        Assert.Contains(methodsSlice.Methods!, method => method is { Name: "plot_rows", Namespace: "R" });

        var calls = methodsSlice.MethodCalls ?? [];
        // Lambda-body calls are attributed to the lambda-assigned function, not the previous one.
        Assert.Contains(calls, call => call is { CalledMethod: "lapply" } && call.SourceId?.Contains("process", StringComparison.Ordinal) == true);
        Assert.Contains(calls, call => call is { CalledMethod: "system" } && call.SourceId?.Contains("render", StringComparison.Ordinal) == true);
        // Comment prose shapes (`word (R 4.1+)`) must not become calls.
        Assert.DoesNotContain(calls, call => call.CalledMethod is "syntax" or "_" or "placeholder" or "shorthand");
    }

    // R 4.4+ added `%||%` and `declare()`, R 4.6 added `%notin%`: the infix operators are not
    // calls and must not mint phantom function names, while the ordinary call keeps flowing
    // through the guard to the `system` sink attribution.
    [SkippableFact]
    public void GetMethods_ModernRSource_InfixOperatorsAreNotCalls()
    {
        SkipIfRNativeParserIsInstalled();
        var methodsSlice = ReadMethods(GetFilePath(ModernRFeatureSource));

        var calls = methodsSlice.MethodCalls ?? [];
        Assert.Contains(calls, call => call is { CalledMethod: "declare" } && call.SourceId?.Contains("render", StringComparison.Ordinal) == true);
        Assert.Contains(calls, call => call is { CalledMethod: "message" } && call.SourceId?.Contains("render", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(calls, call => call.CalledMethod is "%notin%" or "%||%" or "notin");
    }

    // A backslash inside a string literal (`pattern <- "\\d+"`) is not the lambda introducer;
    // treating it as one renamed the enclosing function and mis-attributed its calls.
    [SkippableFact]
    public void GetMethods_ModernRSource_BackslashStringLiteralIsNotALambda()
    {
        SkipIfRNativeParserIsInstalled();
        var methodsSlice = ReadMethods(GetFilePath(ModernRFeatureSource));

        Assert.DoesNotContain(methodsSlice.Methods!, method => method is { Name: "pattern", Namespace: "R" });
        // Calls after the backslash assignment stay attributed to the enclosing function.
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call is { CalledMethod: "grepl" } && call.SourceId?.Contains("render", StringComparison.Ordinal) == true);
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call is { CalledMethod: "system" } && call.SourceId?.Contains("render", StringComparison.Ordinal) == true);
    }

    // R Markdown and Quarto notebooks only contain R inside ```{r} chunks; prose and other
    // engines' chunks must not contribute phantom functions, calls, or dependencies.
    [Fact]
    public void GetMethods_RNotebookSource_AnalyzesOnlyRCodeChunks()
    {
        var methodsSlice = ReadMethods(GetFilePath(NotebookSource));

        Assert.Contains(methodsSlice.Methods!, method => method is { Name: "render", Namespace: "R" });
        Assert.Contains(methodsSlice.Methods!, method => method is { Name: "process", Namespace: "R" });
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency is { Name: "ggplot2" });

        var calls = methodsSlice.MethodCalls ?? [];
        Assert.Contains(calls, call => call is { CalledMethod: "system" } && call.SourceId?.Contains("render", StringComparison.Ordinal) == true);
        Assert.Contains(calls, call => call is { CalledMethod: "sum" } && call.SourceId?.Contains("process", StringComparison.Ordinal) == true);
        // Markdown prose and the python chunk must not leak calls.
        Assert.DoesNotContain(calls, call => call.CalledMethod is "pipeline" or "file.path" or "sum(1" or "readLines" or "os.system" or "import");
    }

    // F# scripts reference packages and files with `#r`/`#load` directives; those are the
    // script equivalent of project references and must surface as dependencies.
    [Fact]
    public void GetMethods_FSharpScript_CollectsReferenceAndLoadDirectives()
    {
        var methodsSlice = ReadMethods(GetFilePath(FSharpScriptSource));

        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency.Name == "Newtonsoft.Json");
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency.Name == "System.Xml");
        Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency.Name == "Helper.fsx");
        Assert.Contains(methodsSlice.Methods!, method => method is { Name: "run" });
    }


    [Fact]
    public void GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true);

        Directory.CreateDirectory(sourceDirectory);
        File.Copy(HelloWorldCSharpSource, Path.Combine(sourceDirectory, HelloWorldCSharpSource), true);
        File.Copy(FooBarCSharpSource, Path.Combine(sourceDirectory, FooBarCSharpSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(24, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        AssertMethods(actualMethods, expectedMethodsFooBarCSharpSource);
        
        // Test inheritance and interface implementation
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
        
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
        
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_VBSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true);

        Directory.CreateDirectory(sourceDirectory);
        File.Copy(HelloWorldVBSource, Path.Combine(sourceDirectory, HelloWorldVBSource), true);
        File.Copy(FooBarVBSource, Path.Combine(sourceDirectory, FooBarVBSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(16, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
        AssertMethods(actualMethods, expectedMethodsFooBarVBSource);
    
        // Test inheritance and interface implementation for VB.NET
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
    
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
    
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
    
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_FSharpSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(fsharpSourceDirectory)) Directory.Delete(fsharpSourceDirectory, true);

        Directory.CreateDirectory(fsharpSourceDirectory);
        File.Copy(HelloWorldFSharpSource, Path.Combine(fsharpSourceDirectory, HelloWorldFSharpSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), fsharpSourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;

        // We expect at least some methods to be detected
        Assert.True(actualMethods?.Count > 0);
        // Check that we have the expected F# functions
        Assert.Contains(actualMethods, m => m.Name == "hello");
        Assert.Contains(actualMethods, m => m.Name == "goodbye");
        Assert.Contains(actualMethods, m => m.Name == "add");
        Assert.Contains(actualMethods, m => m is { ClassName: "Person", Name: "Introduce" });
        Assert.Contains(actualMethods, m => m is { ClassName: "Person", Name: "CelebrateBirthday" });
    }

    [Fact]
    public void GetMethods_CSharpAssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);

        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestDataCSharpDLL, Path.Combine(combinedDirectory, DosaiTestDataCSharpDLL), true);
        File.Copy(HelloWorldCSharpSource, Path.Combine(combinedDirectory, HelloWorldCSharpSource), true);
        File.Copy(FooBarCSharpSource, Path.Combine(combinedDirectory, FooBarCSharpSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(60, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataCSharpDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        AssertMethods(actualMethods, expectedMethodsFooBarCSharpSource);
        
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_VBAssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);

        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestDataVBDLL, Path.Combine(combinedDirectory, DosaiTestDataVBDLL), true);
        File.Copy(HelloWorldVBSource, Path.Combine(combinedDirectory, HelloWorldVBSource), true);
        File.Copy(FooBarVBSource, Path.Combine(combinedDirectory, FooBarVBSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(40, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataVBDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
        AssertMethods(actualMethods, expectedMethodsFooBarVBSource);
    
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_AllLanguages_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(allLanguagesDirectory)) Directory.Delete(allLanguagesDirectory, true);

        Directory.CreateDirectory(allLanguagesDirectory);
        File.Copy(DosaiTestDataCSharpDLL, Path.Combine(allLanguagesDirectory, DosaiTestDataCSharpDLL), true);
        File.Copy(DosaiTestDataVBDLL, Path.Combine(allLanguagesDirectory, DosaiTestDataVBDLL), true);
        File.Copy(HelloWorldCSharpSource, Path.Combine(allLanguagesDirectory, HelloWorldCSharpSource), true);
        File.Copy(FooBarCSharpSource, Path.Combine(allLanguagesDirectory, FooBarCSharpSource), true);
        File.Copy(HelloWorldVBSource, Path.Combine(allLanguagesDirectory, HelloWorldVBSource), true);
        File.Copy(FooBarVBSource, Path.Combine(allLanguagesDirectory, FooBarVBSource), true);
        File.Copy(HelloWorldFSharpSource, Path.Combine(allLanguagesDirectory, HelloWorldFSharpSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), allLanguagesDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;

        Assert.True(actualMethods?.Count > 0);
        // Check that we have methods from all languages
        Assert.Contains(actualMethods, m => m.FileName == DosaiTestDataCSharpDLL);
        Assert.Contains(actualMethods, m => m.FileName == DosaiTestDataVBDLL);
        Assert.Contains(actualMethods, m => m.FileName == HelloWorldCSharpSource);
        Assert.Contains(actualMethods, m => m.FileName == HelloWorldVBSource);
        Assert.Contains(actualMethods, m => m.FileName == HelloWorldFSharpSource);
    }
    
    [Fact]
    public void GetMethods_PathDoesNotExist_ThrowsException()
    {
        var assemblyPath = GetFilePath(FakeDLL);
        Assert.Throws<FileNotFoundException>(() => Depscan.Dosai.GetMethods(assemblyPath));
    }

    [Fact]
    public void GetMethods_PathIsEmptyDirectory_ReturnsNothing()
    {
        Directory.CreateDirectory(emptyDirectory);
        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), emptyDirectory);
        var result = Depscan.Dosai.GetMethods(assemblyFolder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        Assert.Equal(0, methodsSlice?.Methods?.Count);
    }
    #endregion GetMethods

    private static void AssertNamespaces(List<Namespace>? actualNamespaces, Namespace[] expectedNamespaces)
    {
        foreach(var expectedNamespace in expectedNamespaces)
        {
            var troubleshootingMessage = string.Empty;

            try
            {
                troubleshootingMessage = $"{expectedNamespace.FileName}${expectedNamespace.Name}";
                var matchingNamespace = actualNamespaces?.Single(ns => ns.FileName == expectedNamespace.FileName &&
                                                                       ns.Name == expectedNamespace.Name);

                Assert.NotNull(matchingNamespace);
            }
            catch(Exception)
            {
                Assert.Fail($"Matching namespace not found. Expecting: {troubleshootingMessage}");
            }
        }
    }

    private static void AssertMethods(List<Method>? actualMethods, Method[] expectedMethods)
    {
        foreach(var expectedMethod in expectedMethods)
        {
            if (expectedMethod.Name == ".ctor")
            {
                continue;
            }
        
            var matchingMethod = actualMethods?.FirstOrDefault(method => 
                method.FileName == expectedMethod.FileName &&
                method.Namespace == expectedMethod.Namespace &&
                method.ClassName == expectedMethod.ClassName &&
                method.Name == expectedMethod.Name);

            Assert.NotNull(matchingMethod);

            if (expectedMethod.Parameters is not null)
            {
                foreach (var expectedParameter in expectedMethod.Parameters)
                {
                    Assert.True(matchingMethod?.Parameters?.Exists(parameter => parameter.Name == expectedParameter.Name &&
                        parameter.Type == expectedParameter.Type));
                }
            }
        }
    }
    
    private static void AssertMethodCalls(List<MethodCalls>? actualMethodCalls)
    {
        Assert.NotNull(actualMethodCalls);
        if (actualMethodCalls?.Count > 0)
        {
            foreach (var methodCall in actualMethodCalls)
            {
                Assert.NotNull(methodCall.FileName);
                Assert.NotNull(methodCall.CalledMethod);
                Assert.True(methodCall.LineNumber > 0);
                Assert.True(methodCall.ColumnNumber > 0);
            }
        }
    }

    private static string GetFilePath(string filePath)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        return Path.Join(currentDirectory, filePath);
    }

    private static DataFlowResult ReadDataFlows(string path)
    {
        var resultJson = DataFlowAnalyzer.GetDataFlows(path);
        var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(dataFlowResult);
        return dataFlowResult;
    }

    private static MethodsSlice ReadMethods(string path)
    {
        var resultJson = Depscan.Dosai.GetMethods(path);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(resultJson, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(methodsSlice);
        Assert.NotNull(methodsSlice.CallGraph);
        return methodsSlice;
    }

    private static string GenerateManyLocalDeclarations(int count) => string.Join(Environment.NewLine, Enumerable.Range(0, count).Select(index => $"        var filler{index} = {index};"));

    private static string GenerateManyLocalUses(int count) => string.Join(Environment.NewLine, Enumerable.Range(0, count).Select(index => $"        global::System.GC.KeepAlive(filler{index});"));

    private static unsafe int CountDecodedInstructions(string analyzerTypeName, byte[] ilBytes)
    {
        fixed (byte* pointer = ilBytes)
        {
            var reader = new BlobReader(pointer, ilBytes.Length);
            var analyzerType = typeof(Depscan.Dosai).Assembly.GetType(analyzerTypeName, throwOnError: true)!;
            var decodeMethod = analyzerType.GetMethod("DecodeInstructions", BindingFlags.NonPublic | BindingFlags.Static)!;
            var instructions = (IEnumerable)decodeMethod.Invoke(null, [reader])!;
            var count = 0;
            foreach (var _ in instructions)
            {
                count++;
            }
            return count;
        }
    }

    private static string BuildTemporaryProject(string tempRoot, string projectName, string programSource, string targetFramework = "net10.0", string outputType = "Exe")
    {
        var projectDirectory = Path.Combine(tempRoot, projectName, "src");
        var outputDirectory = Path.Combine(tempRoot, projectName, "bin");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, $"{projectName}.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{targetFramework}</TargetFramework>
    <OutputType>{outputType}</OutputType>
    <DebugType>portable</DebugType>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), programSource);

        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{Path.Combine(projectDirectory, $"{projectName}.csproj")}\" -o \"{outputDirectory}\" -v:quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(build);
        // Drain both pipes before waiting: a build that fills either buffer would otherwise
        // block until the test times out.
        var buildStdout = build.StandardOutput.ReadToEndAsync();
        var buildStderr = build.StandardError.ReadToEndAsync();
        build.WaitForExit();
        var buildOutput = buildStdout.GetAwaiter().GetResult() + buildStderr.GetAwaiter().GetResult();
        Assert.True(build.ExitCode == 0, buildOutput);
        return outputDirectory;
    }

    /// <summary>
    ///     Tests that compile .NET 11-only source (C# 15 union declarations) at test time need
    ///     an 11.x SDK on the machine; they skip otherwise so older environments only lose
    ///     coverage instead of failing. Lazy so the probe runs once even though xUnit may reach
    ///     it from several test threads.
    /// </summary>
    private static readonly Lazy<bool> Net11SdkAvailable = new(() =>
    {
        using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--list-sdks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        if (probe is null)
        {
            return false;
        }

        // Read before waiting: a probe that fills the pipe buffer would otherwise block forever.
        var output = probe.StandardOutput.ReadToEnd();
        probe.WaitForExit();
        return probe.ExitCode == 0 && output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Lines look like "11.0.100-rc.1.26425.128 [/path]" or "11.0.100 [/path]"; the
            // numeric component precedes any prerelease suffix and the path bracket.
            .Any(line => Version.TryParse(line.Split(['[', '-'])[0].Trim(), out var version) && version.Major >= 11);
    });

    private static bool HasNet11Sdk() => Net11SdkAvailable.Value;

    // The R frontend prefers Rscript's own parser and only falls back to lexical extraction when
    // R is absent, so tests written against the fallback must not run against the native parser -
    // they would silently assert the wrong code path.
    private static void SkipIfRNativeParserIsInstalled() =>
        Skip.If(LanguageFrontendAnalyzer.IsRNativeParserAvailable, "Rscript is installed, so the managed R fallback parser is not the code path under test.");

    private static void WriteProjectAssets(string directory, string packageName, string version, string assemblyFileName)
    {
        Directory.CreateDirectory(Path.Combine(directory, "obj"));
        File.WriteAllText(Path.Combine(directory, "obj", "project.assets.json"), $$"""
{
  "version": 3,
  "targets": {
    "net10.0": {
      "{{packageName}}/{{version}}": {
        "type": "package",
        "compile": {
          "lib/netstandard2.0/{{assemblyFileName}}": {}
        },
        "runtime": {
          "lib/netstandard2.0/{{assemblyFileName}}": {}
        }
      }
    }
  },
  "libraries": {
    "{{packageName}}/{{version}}": {
      "sha512": "",
      "type": "package",
      "path": "{{packageName.ToLowerInvariant()}}/{{version}}"
    }
  }
}
""");
    }

    [Fact]
    public void Methods_RestoreCacheOutput_BindsPackageCallsWithoutBuildOutput()
    {
        using var tempDirectory = new TemporaryDirectory();
        // The package assembly must come from the NuGet cache alone: it is copied to a
        // directory OUTSIDE the analyzed path, exactly like the real global packages folder,
        // and the analyzed tree has no bin/ build output of its own.
        var libraryOutput = BuildTemporaryProject(tempDirectory.Path, "RestoreSampleLib", """
namespace RestoreSampleLib
{
    public static class Greeter
    {
        public static string Hello() => "hello";
    }
}
""", outputType: "Library");
        var packageCacheRoot = Path.Combine(tempDirectory.Path, "package-cache");
        var packageLibDirectory = Path.Combine(packageCacheRoot, "restore-sample-lib", "1.0.0", "lib", "net10.0");
        Directory.CreateDirectory(packageLibDirectory);
        File.Copy(Path.Combine(libraryOutput, "RestoreSampleLib.dll"), Path.Combine(packageLibDirectory, "RestoreSampleLib.dll"));

        var analyzedDirectory = Path.Combine(tempDirectory.Path, "app", "src");
        Directory.CreateDirectory(Path.Combine(analyzedDirectory, "obj"));
        File.WriteAllText(Path.Combine(analyzedDirectory, "Program.cs"), """
using RestoreSampleLib;

internal static class Program
{
    private static void Main()
    {
        _ = Greeter.Hello();
    }
}
""");
        // Forward slashes inside the JSON: Windows path separators would need escaping, and
        // the resolver accepts either separator.
        File.WriteAllText(Path.Combine(analyzedDirectory, "obj", "project.assets.json"), $$"""
{
  "version": 3,
  "project": { "version": "1.0.0" },
  "packageFolders": { "{{packageCacheRoot.Replace('\\', '/')}}/": {} },
  "libraries": {
    "RestoreSampleLib/1.0.0": { "type": "package", "path": "restore-sample-lib/1.0.0" }
  },
  "targets": {
    "net10.0": {
      "RestoreSampleLib/1.0.0": {
        "type": "package",
        "compile": { "lib/net10.0/RestoreSampleLib.dll": {} },
        "runtime": { "lib/net10.0/RestoreSampleLib.dll": {} }
      }
    }
  }
}
""");

        var methodsSlice = Depscan.Dosai.GetMethodsSlice(analyzedDirectory);

        var boundCall = Assert.Single(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect && call.TargetId!.Contains("Greeter.Hello", StringComparison.Ordinal));
        Assert.Equal("pkg:nuget/RestoreSampleLib@1.0.0", boundCall.Purl);
        var reachability = Assert.Single(methodsSlice.PackageReachability!, package => package.Purl == "pkg:nuget/RestoreSampleLib@1.0.0");
        Assert.Equal("ExternalCallGraphNode", reachability.ReachabilityKind);
        Assert.Equal("High", reachability.Confidence);
        Assert.Contains(AnalysisEvidenceKind.SourceRoslynDirect, reachability.EvidenceKinds);
        Assert.Contains(methodsSlice.Diagnostics!, diagnostic => diagnostic.Contains("Resolved 1 package assemblies from NuGet restore output", StringComparison.Ordinal));

        var dataFlowResult = DataFlowAnalyzer.Analyze(analyzedDirectory);
        Assert.Contains(dataFlowResult.Diagnostics, diagnostic => diagnostic.Contains("Resolved 1 package assemblies from NuGet restore output", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_UnresolvedQualifiedCall_MapsNamespaceToPackageReachability()
    {
        using var tempDirectory = new TemporaryDirectory();
        var analyzedDirectory = Path.Combine(tempDirectory.Path, "src");
        Directory.CreateDirectory(Path.Combine(analyzedDirectory, "obj"));
        // Console.WriteLine receives the poisoned (error-typed) payload, so overload
        // resolution fails there too - it keeps its candidates and must NOT be blamed as an
        // unresolved target; only the genuinely missing JsonConvert call site is.
        File.WriteAllText(Path.Combine(analyzedDirectory, "Program.cs"), """
internal static class Program
{
    private static string Payload()
    {
        var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { name = "dosai" });
        System.Console.WriteLine(payload);
        return payload;
    }
}
""");
        File.WriteAllText(Path.Combine(analyzedDirectory, "obj", "project.assets.json"), $$"""
{
  "version": 3,
  "project": { "version": "1.0.0" },
  "packageFolders": { "{{Path.Combine(tempDirectory.Path, "missing-packages").Replace('\\', '/')}}/": {} },
  "libraries": {
    "Newtonsoft.Json/12.0.3": { "type": "package", "path": "newtonsoft.json/12.0.3" }
  },
  "targets": {
    "net8.0": {
      "Newtonsoft.Json/12.0.3": {
        "type": "package",
        "compile": { "lib/netstandard2.0/Newtonsoft.Json.dll": {} }
      }
    }
  }
}
""");

        var methodsSlice = Depscan.Dosai.GetMethodsSlice(analyzedDirectory);

        // The receiver's own qualification ("Newtonsoft.Json.JsonConvert") grounds the
        // namespace, so the unresolved call still maps to the package.
        var unresolvedCall = Assert.Single(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved);
        Assert.Equal("Unresolved:Newtonsoft.Json.JsonConvert.SerializeObject", unresolvedCall.TargetId);
        Assert.Equal("Newtonsoft.Json", unresolvedCall.Namespace);
        Assert.Equal("pkg:nuget/Newtonsoft.Json@12.0.3", unresolvedCall.Purl);
        Assert.DoesNotContain(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved && call.TargetId!.StartsWith("Unresolved:System.Console", StringComparison.Ordinal));
        var reachability = Assert.Single(methodsSlice.PackageReachability!, package => package.Purl == "pkg:nuget/Newtonsoft.Json@12.0.3");
        Assert.Equal("ExternalCallGraphNode", reachability.ReachabilityKind);
        Assert.Equal("Low", reachability.Confidence);
        Assert.Contains(AnalysisEvidenceKind.SourceUnresolved, reachability.EvidenceKinds);
        Assert.Contains(reachability.ConfidenceReasons, reason => reason.Contains("Package assemblies were not available", StringComparison.Ordinal));
        Assert.Contains(methodsSlice.Diagnostics!, diagnostic => diagnostic.Contains("Semantic binding failed for 1 call sites", StringComparison.Ordinal));
        Assert.Contains(methodsSlice.Diagnostics!, diagnostic => diagnostic.Contains("Found project.assets.json but no package assemblies", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_UnresolvedUnqualifiedCall_DoesNotFabricatePackageReachability()
    {
        using var tempDirectory = new TemporaryDirectory();
        var analyzedDirectory = Path.Combine(tempDirectory.Path, "src");
        Directory.CreateDirectory(Path.Combine(analyzedDirectory, "obj"));
        // Neither receiver states a namespace: an unqualified missing type, and a type that
        // exists in no package at all. The file's single non-System using must NOT get the
        // blame - attributing it would promote an innocent package from dependency-only to
        // reachable, and ReachabilityKind is what downstream consumers trust.
        File.WriteAllText(Path.Combine(analyzedDirectory, "Program.cs"), """
using Newtonsoft.Json;

internal static class Program
{
    private static string Payload()
    {
        var payload = JsonConvert.SerializeObject(new { name = "dosai" });
        return TotallyMissingHelper.Decorate(payload);
    }
}
""");
        File.WriteAllText(Path.Combine(analyzedDirectory, "obj", "project.assets.json"), $$"""
{
  "version": 3,
  "project": { "version": "1.0.0" },
  "packageFolders": { "{{Path.Combine(tempDirectory.Path, "missing-packages").Replace('\\', '/')}}/": {} },
  "libraries": {
    "Newtonsoft.Json/12.0.3": { "type": "package", "path": "newtonsoft.json/12.0.3" }
  },
  "targets": {
    "net8.0": {
      "Newtonsoft.Json/12.0.3": {
        "type": "package",
        "compile": { "lib/netstandard2.0/Newtonsoft.Json.dll": {} }
      }
    }
  }
}
""");

        var methodsSlice = Depscan.Dosai.GetMethodsSlice(analyzedDirectory);

        // The unresolved call sites are still recorded (not silently dropped)...
        Assert.Contains(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved && call.TargetId == "Unresolved:JsonConvert.SerializeObject");
        Assert.Contains(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved && call.TargetId == "Unresolved:TotallyMissingHelper.Decorate");
        // ...but none of them claims a namespace or a package purl.
        Assert.DoesNotContain(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved && (!string.IsNullOrWhiteSpace(call.Namespace) || !string.IsNullOrWhiteSpace(call.Purl)));
        // The package stays at the dependency-only fallback with no unresolved evidence.
        var reachability = Assert.Single(methodsSlice.PackageReachability!, package => package.Purl == "pkg:nuget/Newtonsoft.Json@12.0.3");
        Assert.Equal("Dependency", reachability.ReachabilityKind);
        Assert.DoesNotContain(AnalysisEvidenceKind.SourceUnresolved, reachability.EvidenceKinds);
        Assert.DoesNotContain(reachability.ConfidenceReasons, reason => reason.Contains("Package assemblies were not available", StringComparison.Ordinal));
        // The diagnostic still tells the operator why binding failed.
        Assert.Contains(methodsSlice.Diagnostics!, diagnostic => diagnostic.Contains("Semantic binding failed for", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_UnresolvedValueChainCall_DoesNotRecordReceiverAsNamespace()
    {
        using var tempDirectory = new TemporaryDirectory();
        var analyzedDirectory = Path.Combine(tempDirectory.Path, "src");
        Directory.CreateDirectory(analyzedDirectory);
        // The receivers are value chains, not namespace qualifications: a local, a field and a
        // parameter, each of an unresolvable type. The head of a value chain is not a
        // namespace, so recording it as one would put noise in every such edge's Namespace.
        File.WriteAllText(Path.Combine(analyzedDirectory, "Program.cs"), """
internal static class Program
{
    private static MissingClient shared = null!;

    private static void Run(MissingClient injected)
    {
        var client = shared;
        client.Inner.Send("a");
        shared.Inner.Send("b");
        injected.Inner.Send("c");
    }
}
""");

        var methodsSlice = Depscan.Dosai.GetMethodsSlice(analyzedDirectory);

        var unresolvedCalls = methodsSlice.MethodCalls!
            .Where(call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved)
            .ToList();
        Assert.NotEmpty(unresolvedCalls);
        // Every edge is recorded, and none of them claims "client", "shared" or "injected" as
        // a namespace (nor resolves a purl off one).
        Assert.All(unresolvedCalls, call =>
        {
            Assert.True(string.IsNullOrWhiteSpace(call.Namespace), $"Unexpected namespace '{call.Namespace}' on {call.TargetId}");
            Assert.True(string.IsNullOrWhiteSpace(call.Purl), $"Unexpected purl '{call.Purl}' on {call.TargetId}");
        });
    }

    [Fact]
    public void BuildPreparation_Restore_MakesLocalPackageAvailableForBinding()
    {
        using var tempDirectory = new TemporaryDirectory();
        // Pack a local library into a folder NuGet source so restore stays offline.
        var libraryDirectory = Path.Combine(tempDirectory.Path, "LocalLib");
        Directory.CreateDirectory(libraryDirectory);
        File.WriteAllText(Path.Combine(libraryDirectory, "LocalLib.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>LocalLib</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(libraryDirectory, "Greeter.cs"), """
namespace LocalLib
{
    public static class Greeter
    {
        public static string Hello() => "hello";
    }
}
""");
        var packagesDirectory = Path.Combine(tempDirectory.Path, "nupkg");
        Directory.CreateDirectory(packagesDirectory);
        RunDotNet($"pack \"{Path.Combine(libraryDirectory, "LocalLib.csproj")}\" -o \"{packagesDirectory}\" -v:quiet --nologo");

        var appDirectory = Path.Combine(tempDirectory.Path, "App");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "nuget.config"), $"""
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="{packagesDirectory}" />
  </packageSources>
</configuration>
""");
        File.WriteAllText(Path.Combine(appDirectory, "App.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LocalLib" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(appDirectory, "Program.cs"), """
using LocalLib;

internal static class Program
{
    private static void Main()
    {
        _ = Greeter.Hello();
    }
}
""");

        BuildPreparation.Prepare(appDirectory, BuildPreparationMode.Restore);

        Assert.True(File.Exists(Path.Combine(appDirectory, "obj", "project.assets.json")), "dotnet restore should create obj/project.assets.json");
        var methodsSlice = Depscan.Dosai.GetMethodsSlice(appDirectory);
        Assert.Contains(methodsSlice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect && call.TargetId!.Contains("Greeter.Hello", StringComparison.Ordinal));
        Assert.Contains(methodsSlice.PackageReachability!, package => package.Purl == "pkg:nuget/LocalLib@1.0.0" && package.Confidence == "High");
    }

    [Fact]
    public void BuildPreparation_WithoutProjectsOrInvalidPaths_IsSafeNoOp()
    {
        using var tempDirectory = new TemporaryDirectory();
        // No solution or project files: restore finds nothing to run and must not throw.
        BuildPreparation.Prepare(tempDirectory.Path, BuildPreparationMode.Restore);
        BuildPreparation.Prepare(tempDirectory.Path, BuildPreparationMode.None);
        var filePath = Path.Combine(tempDirectory.Path, "not-a-project.txt");
        File.WriteAllText(filePath, "hello");
        BuildPreparation.Prepare(filePath, BuildPreparationMode.Restore);
        BuildPreparation.Prepare(Path.Combine(tempDirectory.Path, "missing"), BuildPreparationMode.Restore);
    }

    private static void RunDotNet(string arguments)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        // Drain both pipes before waiting: a verbose pack that fills either buffer would
        // otherwise block until the test times out.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, output);
    }

    // Expected namespaces in Dosai.TestData.CSharp.dll
    private static readonly Namespace[] expectedNamespacesDosaiTestDataCSharpDLL =
    [
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Name = "FooBar"
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Name = "HelloWorld"
        }
    ];

    // Expected namespaces in HelloWorld.cs
    private static readonly Namespace[] expectedNamespacesHelloWorldCSharpSource =
    [
        new()
        {
            FileName = "HelloWorld.cs",
            Name = "HelloWorld"
        }
    ];

    // Expected namespaces in FooBar.cs
    private static readonly Namespace[] expectedNamespacesFooBarCSharpSource =
    [
        new()
        {
            FileName = "FooBar.cs",
            Name = "FooBar"
        }
    ];

    // Expected methods in Dosai.TestData.CSharp.dll
    private static readonly Method[] expectedMethodsDosaiTestDataCSharpDLL =
    [
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "FooBar",
            ClassName = "Foo",
            Attributes = "Public, Static, HideBySig",
            Name = "Main",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "System.String[]"
                }
            ]
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "FooBar",
            ClassName = "Bar",
            Attributes = "Public, HideBySig",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Static, HideBySig",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, HideBySig",
            Name = "Appreciate",
            ReturnType = "Task",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public, HideBySig",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        }
    ];

    // Expected methods in Dosai.TestData.VB.dll
    private static readonly Method[] expectedMethodsDosaiTestDataVBDLL =
    [
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.FooBar",
            ClassName = "Foo",
            Attributes = "Public, Static",
            Name = "Main",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "System.String[]"
                }
            ]
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.FooBar",
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.HelloWorld",
            ClassName = "Hello",
            Attributes = "Public",
            Name = "Appreciate",
            ReturnType = "Task",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        }
    ];

    // Expected methods in HelloWorld.cs
    private static readonly Method[] expectedMethodsHelloWorldCSharpSource =
    [
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = 19,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 24,
            ColumnNumber = 9,
            ReturnType = "Task",
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public",
            Name = "InterfaceMethod",
            ReturnType = "Void",
            LineNumber = 29,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Override",
            Name = "BaseMethod",
            ReturnType = "Void",
            LineNumber = 33,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "InterfaceMethod",
            ReturnType = "Void",
            LineNumber = 43,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = 39,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in HelloWorld.vb
    private static readonly Method[] expectedMethodsHelloWorldVBSource =
    [
        new()
        {
            FileName = "HelloWorld.vb",
            Assembly = "HelloWorld.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.vb.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Shared",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = 7,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.vb",
            Assembly = "HelloWorld.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.vb.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 10,
            ColumnNumber = 9,
            ReturnType = "Task",
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.vb",
            Assembly = "HelloWorld.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.vb.exe",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = 16,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in FooBar.cs
    private static readonly Method[] expectedMethodsFooBarCSharpSource =
    [
        new()
        {
            FileName = "FooBar.cs",
            Assembly = "FooBar.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.cs.exe",
            Namespace = "FooBar",
            ClassName = "Foo",
            Attributes = "Public, Static",
            Name = "Main",
            ReturnType = "Void",
            LineNumber = 7,
            ColumnNumber = 9,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "String[]"
                }
            ]
        },
        new()
        {
            FileName = "FooBar.cs",
            Assembly = "FooBar.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.cs.exe",
            Namespace = "FooBar",
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = 15,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in FooBar.vb
    private static readonly Method[] expectedMethodsFooBarVBSource =
    [
        new()
        {
            FileName = "FooBar.vb",
            Assembly = "FooBar.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.vb.exe",
            Namespace = "FooBar",
            ClassName = "Foo",
            Attributes = "Public, Shared",
            Name = "Main",
            ReturnType = "Void",
            LineNumber = 5,
            ColumnNumber = 9,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "String()"
                }
            ]
        },
        new()
        {
            FileName = "FooBar.vb",
            Assembly = "FooBar.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.vb.exe",
            Namespace = "FooBar",
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = 10,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    private const string DosaiTestDataCSharpDLL = "Dosai.TestData.CSharp.dll";
    private const string DosaiTestDataVBDLL = "Dosai.TestData.VB.dll";
    private const string DosaiTestsPdb = "Dosai.Tests.pdb";
    private const string HelloWorldCSharpSource = "HelloWorld.cs";
    private const string FooBarCSharpSource = "FooBar.cs";
    private const string HelloWorldVBSource = "HelloWorld.vb";
    private const string FooBarVBSource = "FooBar.vb";
    private const string HelloWorldFSharpSource = "HelloWorld.fs";
    private const string FSharp11FeaturesSource = "FSharp11Features.fs";
    private const string FSharp11MoreFeaturesSource = "FSharp11MoreFeatures.fs";
    private const string FSharp11LanguageSource = "FSharp11Language.fs";
    private const string CSharp15FeatureSource = "CSharp15Features.cs";
    private const string FileBasedAppSource = "FileBasedApp.cs";
    private const string ExtensionMemberSource = "ExtensionMembers.cs";
    private const string ModernRFeatureSource = "ModernRFeatures.R";
    private const string NotebookSource = "Notebook.Rmd";
    private const string FSharpScriptSource = "FSharpScript.fsx";
    private const string FSharpModuleLayoutsSource = "FSharpModuleLayouts.fs";
    private const string FakeDLL = "Fake.dll";
    private const string sourceDirectory = "source";
    private const string fsharpSourceDirectory = "fsharp-source";
    private const string emptyDirectory = "empty";
    private const string combinedDirectory = "combined";
    private const string allLanguagesDirectory = "all-languages";

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}