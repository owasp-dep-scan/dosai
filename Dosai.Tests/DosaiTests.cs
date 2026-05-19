using Depscan;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Xunit;

namespace Dosai.Tests;

public class DosaiTests
{
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
            edge.SourceId == "HelloWorld.Hello.Appreciate():System.Threading.Tasks.Task" &&
            edge.TargetId == "System.Threading.Tasks.Task.Delay(int):System.Threading.Tasks.Task" &&
            edge.CallType == CallType.MethodCall);
        Assert.Contains(callGraph.Edges, edge =>
            edge.SourceId == "HelloWorld.GenericProcessor<T>..ctor(T)" &&
            edge.TargetId == "HelloWorld.GenericProcessor<T>.set_Value(T):void" &&
            edge.CallType == CallType.PropertySet);
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
        Assert.Contains(dataFlowResult.Nodes, node => node.IsSource && node.Category == "cli" && node.FileName == "FlowSample.cs" && node.LineNumber > 0);
        Assert.Contains(dataFlowResult.Nodes, node => node.IsSink && node.Category == "command" && node.Symbol is not null && node.Symbol.Contains("System.Diagnostics.Process.Start"));
        Assert.All(dataFlowResult.Edges, edge =>
        {
            Assert.Contains(dataFlowResult.Nodes, node => node.Id == edge.SourceId);
            Assert.Contains(dataFlowResult.Nodes, node => node.Id == edge.TargetId);
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

        Assert.Contains(result.Assets, asset => asset.Name == "MD5" && asset.Strength == "weak");
        Assert.Contains(result.Materials, material => material.Storage == "hardcoded" && material.Fingerprint is not null);
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-MD5");
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-MD5" && finding.ReachableFromEntryPoint);
        Assert.Contains(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED");

        var cdx = CryptoAnalyzer.GetCryptoAnalysis(tempDirectory.Path, "cyclonedx");
        using var document = JsonDocument.Parse(cdx);
        Assert.Equal("CycloneDX", document.RootElement.GetProperty("bomFormat").GetString());
        Assert.True(document.RootElement.GetProperty("components").GetArrayLength() >= 1);
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
        Assert.Contains(methodsSlice!.Methods ?? [], method => method.Module == "FSharp.Compiler.Service" && method.Name == "run");
        Assert.Contains(methodsSlice!.Methods ?? [], method => method.Name == "run" && method.Namespace == "R" && method.Module is "R.NativeParser" or "LanguageFrontend");
        if (LanguageFrontendAnalyzer.IsRNativeParserAvailable)
        {
            Assert.Contains(methodsSlice.Methods ?? [], method => method.Name == "run" && method.Module == "R.NativeParser");
            Assert.Contains(methodsSlice.Dependencies ?? [], dependency => dependency.Name == "DBI" && dependency.Module == "R.NativeParser");
        }
        Assert.Contains(methodsSlice.Methods ?? [], method => method.Module == "VC++" && method.Name == "main");
        Assert.Contains(methodsSlice.MethodCalls ?? [], call => call.CalledMethod == "system");
        Assert.NotNull(methodsSlice.CallGraph);
        var nodeIds = methodsSlice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(methodsSlice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
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
        Assert.Contains(result!.Slices, slice => slice.SinkCategory == "command" && slice.Confidence == "Low");
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
        Assert.Contains(dataFlowResult.Nodes, node => node.IsSource && node.Category == "custom-source");
        Assert.Contains(dataFlowResult.Nodes, node => node.IsSink && node.Category == "custom-sink");
        Assert.Contains(dataFlowResult.Slices, slice => slice.SourceCategory == "custom-source" && slice.SinkCategory == "custom-sink");
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
        Assert.Contains(result.Nodes, node => node.IsSource && node.Purl == "pkg:nuget/Input.Package@1.0.0");
        Assert.Contains(result.Nodes, node => node.IsSink && node.Purl == "pkg:nuget/Dangerous.Package@2.0.0");
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
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "file" && slice.SinkArgumentIndex == -1);
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
class AuthorizeAttribute : Attribute { public string? Policy { get; set; } public string? Roles { get; set; } public string? AuthenticationSchemes { get; set; } }
class RequiredScopeAttribute : Attribute { public RequiredScopeAttribute(string scope) { } }
class EnableCorsAttribute : Attribute { public EnableCorsAttribute(string policy) { } }
class ValidateAntiForgeryTokenAttribute : Attribute { }

[Route("api/orders")]
[Authorize(Roles = "Admin,Auditor", Policy = "OrdersPolicy", AuthenticationSchemes = "Bearer")]
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
        Assert.Contains(methodsSlice?.EntryPoints ?? [], entryPoint => entryPoint.Route == "api/orders/{id}" && entryPoint.AuthorizationRequired == true && entryPoint.Roles.Contains("Admin"));
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
        var evidenceOutput = Path.Combine(tempDirectory.Path, "evidence.json");

        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", nativeOutput, "--format", "dosai"]));
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", cdxOutput, "--format", "cyclonedx"]));
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", evidenceOutput, "--format", "cdxgen-evidence"]));

        using var nativeDocument = JsonDocument.Parse(File.ReadAllText(nativeOutput));
        Assert.True(nativeDocument.RootElement.GetProperty("Statistics").GetProperty("ReachableFindingCount").GetInt32() >= 1);

        var findingQuery = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(File.ReadAllText(nativeOutput), "findings[ruleId~=MD5]"));
        var assetQuery = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(File.ReadAllText(nativeOutput), "assets[family=hash]"));
        Assert.NotNull(findingQuery);
        Assert.NotEmpty(findingQuery);
        Assert.NotNull(assetQuery);
        Assert.NotEmpty(assetQuery);

        using var cdxDocument = JsonDocument.Parse(File.ReadAllText(cdxOutput));
        Assert.Equal("CycloneDX", cdxDocument.RootElement.GetProperty("bomFormat").GetString());
        Assert.True(cdxDocument.RootElement.GetProperty("components").GetArrayLength() >= 1);
        Assert.True(cdxDocument.RootElement.GetProperty("vulnerabilities").GetArrayLength() >= 1);

        using var evidenceDocument = JsonDocument.Parse(File.ReadAllText(evidenceOutput));
        Assert.Equal("crypto-evidence", evidenceDocument.RootElement.GetProperty("type").GetString());
        Assert.True(evidenceDocument.RootElement.GetProperty("findings").GetArrayLength() >= 1);

        using var input = new StringReader("""
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.crypto","arguments":{"format":"cyclonedx"}}}

""");
        using var output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, input, output);
        Assert.Contains("CycloneDX", output.ToString());
        Assert.Contains("dosai:crypto:reachableFromEntryPoint", output.ToString());
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
        Assert.Contains(result.Nodes, node => node.IsSource && node.Category == "cli" && node.FileName == "VbFlow.vb");
        Assert.Contains(result.Nodes, node => node.IsSink && node.Category == "command" && node.FileName == "VbFlow.vb");
        Assert.Contains(result.Slices, slice => slice.SourceCategory == "cli" && slice.SinkCategory == "command");
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
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint.HttpMethod == "GET" && endpoint.Route == "api/[controller]/{id}" && endpoint.Urls.Contains("https://api.example.test/orders/"));
        Assert.Contains(methodsSlice.ApiEndpoints ?? [], endpoint => endpoint.HttpMethod == "POST" && endpoint.Route == "/upload" && endpoint.EndpointKind == "MinimalApi");
        Assert.NotNull(methodsSlice.Metadata);
        Assert.Contains(methodsSlice.EntryPoints ?? [], entryPoint => entryPoint.Kind == "HttpEndpoint" && entryPoint.Route == "api/[controller]/{id}");
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
        Assert.Contains(result.EntryPoints, entryPoint => entryPoint.Kind == "Cli" && entryPoint.MethodName == "Main");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness.Kind == "CommandInjectionCandidate" && weakness.Cwe == "CWE-78");
        Assert.Contains(result.DangerousApiReachability, api => api.Category == "command");

        var context = TransparencyBuilder.BuildAgentContext(result, tempDirectory.Path);
        Assert.Contains(context.HighRiskWeaknesses, weakness => weakness.Kind == "CommandInjectionCandidate");
        Assert.Contains("Dosai found", context.Summary);

        var markdown = TransparencyBuilder.ToMarkdownReport(result);
        Assert.Contains("CommandInjectionCandidate", markdown);
        Assert.Contains("WeaknessFlow.cs", markdown);
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
        Assert.Contains(dataFlowResult.Nodes, node => node.IsSink && node.Purl == "pkg:nuget/Microsoft.Data.SqlClient@5.1.1");
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
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "Introduce");
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "CelebrateBirthday");
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
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "Introduce");
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "CelebrateBirthday");
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