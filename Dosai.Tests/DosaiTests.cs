using Depscan;
using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var projectDirectory = Path.Combine(tempDirectory.Path, "src");
        var outputDirectory = Path.Combine(tempDirectory.Path, "bin");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "AssemblyOnlyFlow.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), """
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

        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{Path.Combine(projectDirectory, "AssemblyOnlyFlow.csproj")}\" -o \"{outputDirectory}\" -v:quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(build);
        build.WaitForExit();
        var buildOutput = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        Assert.True(build.ExitCode == 0, buildOutput);

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
using System.Security.Cryptography;
using System.Text;

class CryptoSample
{
    static string StaticKey = "0123456789abcdef0123456789abcdef";

    static void Main(string[] args)
    {
        Hash(args[0]);
    }

    public static byte[] Hash(string input)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(Encoding.UTF8.GetBytes(input));
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
        Assert.Contains(result.Materials, material => material is { Storage: "hardcoded", Fingerprint: not null });
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-MD5");
        Assert.Contains(result.Findings, finding => finding is { RuleId: "DOSAI-CRYPTO-WEAK-HASH-MD5", ReachableFromEntryPoint: true });
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED");

        var cdx = CryptoAnalyzer.GetCryptoAnalysis(tempDirectory.Path, "cyclonedx");
        using var document = JsonDocument.Parse(cdx);
        Assert.Equal("CycloneDX", document.RootElement.GetProperty("bomFormat").GetString());
        var components = document.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.True(components.Count >= 1);
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:evidenceType", "asset"));
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:evidenceType", "operation"));
        Assert.Contains(components, component => HasProperty(component, "dosai:crypto:evidenceType", "material"));

        static bool HasProperty(JsonElement component, string name, string value)
        {
            return component.TryGetProperty("properties", out var properties)
                   && properties.EnumerateArray().Any(property => property.GetProperty("name").GetString() == name && property.GetProperty("value").GetString() == value);
        }
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
        Assert.Contains(methodsSlice?.EntryPoints ?? [], entryPoint => entryPoint is { Route: "api/orders/{id}", AuthorizationRequired: true } && entryPoint.Roles.Contains("Admin"));
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
        McpServer.Run(tempDirectory.Path, null, null, input, output);
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
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", cdxOutput, "--format", "cyclonedx"]));
        Assert.Equal(1, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", unsupportedFormatOutput, "--format", "unsupported"]));

        using var nativeDocument = JsonDocument.Parse(File.ReadAllText(nativeOutput));
        Assert.True(nativeDocument.RootElement.GetProperty("Statistics").GetProperty("ReachableFindingCount").GetInt32() >= 1);

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

        using var input = new StringReader("""
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.crypto","arguments":{"format":"cyclonedx"}}}

""");
        using var output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, input, output);
        Assert.Contains("CycloneDX", output.ToString());
        Assert.Contains("dosai:crypto:reachableFromEntryPoint", output.ToString());

        static bool HasProperty(JsonElement component, string name, string value)
        {
            return component.TryGetProperty("properties", out var properties)
                   && properties.EnumerateArray().Any(property => property.GetProperty("name").GetString() == name && property.GetProperty("value").GetString() == value);
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
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { HttpMethod: "GET", Route: "api/[controller]/{id}" } && endpoint.Urls.Contains("https://api.example.test/orders/"));
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint is { HttpMethod: "POST", Route: "/upload", EndpointKind: "MinimalApi" });
        Assert.NotNull(methodsSlice.Metadata);
        Assert.Contains(methodsSlice.EntryPoints ?? [], entryPoint => entryPoint is { Kind: "HttpEndpoint", Route: "api/[controller]/{id}" });
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

    private static string BuildTemporaryProject(string tempRoot, string projectName, string programSource)
    {
        var projectDirectory = Path.Combine(tempRoot, projectName, "src");
        var outputDirectory = Path.Combine(tempRoot, projectName, "bin");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, $"{projectName}.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
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
        build.WaitForExit();
        var buildOutput = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        Assert.True(build.ExitCode == 0, buildOutput);
        return outputDirectory;
    }

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