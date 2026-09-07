using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;

namespace Dosai.Tests.Roadmap;

/// <summary>Shared helpers for roadmap-batch tests: temp projects, source fixtures, analyzer entry points.</summary>
public sealed class RoadmapTemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

    public RoadmapTemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    public string WriteSource(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    public DataFlowResult DataFlows(string? patternPacks = null) => DataFlowAnalyzer.Analyze(Path, null, patternPacks);

    public MethodsSlice Methods() => JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(Path), JsonOptions)!;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Builds a temporary project and returns the bin directory containing the built DLL (mirrors DosaiTests.BuildTemporaryProject for IL-mode tests).</summary>
    public string BuildTemporaryProject(string projectName, string programSource)
    {
        var projectDirectory = System.IO.Path.Combine(Path, projectName, "src");
        var outputDirectory = System.IO.Path.Combine(Path, projectName, "bin");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(System.IO.Path.Combine(projectDirectory, $"{projectName}.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <DebugType>portable</DebugType>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(System.IO.Path.Combine(projectDirectory, "Program.cs"), programSource);

        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{System.IO.Path.Combine(projectDirectory, $"{projectName}.csproj")}\" -o \"{outputDirectory}\" -v:quiet",
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
}

/// <summary>Stub types that let symbol-resolved patterns (Method kind) match without package references.</summary>
public static class RoadmapStubs
{
    public const string MvcStubs = """
namespace Microsoft.AspNetCore.Mvc
{
    public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
    public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
    public class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
    public class HttpPutAttribute : System.Attribute { public HttpPutAttribute() { } }
    public class HttpDeleteAttribute : System.Attribute { public HttpDeleteAttribute() { } }
    public class AllowAnonymousAttribute : System.Attribute { }
    public class AuthorizeAttribute : System.Attribute { public AuthorizeAttribute() { } }
    public class FromBodyAttribute : System.Attribute { }
    public class FromQueryAttribute : System.Attribute { }
    public class ControllerBase { public virtual Microsoft.AspNetCore.Mvc.IActionResult Ok(object value) => null; }
    public interface IActionResult { }
}
""";

    public const string HtmlStubs = """
namespace Microsoft.AspNetCore.Html
{
    public interface IHtmlHelper { Microsoft.AspNetCore.Html.IHtmlContent Raw(string value); }
    public interface IHtmlContent { }
}
namespace System.Text.Encodings.Web
{
    public static class HtmlEncoderStub { public static string Encode(string value) => value; }
}
""";

    // System.Xml (XmlDocument/XDocument/XmlReader) ships in the shared framework and resolves
    // natively; stubbing it would only create ambiguity, so no XmlStubs here.
    public const string DirectoryServicesStubs = """
namespace System.DirectoryServices
{
    public class DirectoryEntry { public DirectoryEntry(string path) { } }
    public class DirectorySearcher { public DirectorySearcher(string filter) { } }
}
""";

    public const string MongoStubs = """
namespace MongoDB.Bson
{
    public class BsonDocument { public static MongoDB.Bson.BsonDocument Parse(string json) => null; }
}
""";

    // W4 sinks mirror the REAL API shapes: ILogger.Log* and IHeaderDictionary.Append are
    // extension methods (Microsoft.Extensions.Logging.LoggerExtensions /
    // Microsoft.AspNetCore.Http.HeaderDictionaryExtensions), so the stubs declare extension
    // classes — instance-method stubs would silently pass against the wrong pattern anchor.
    public const string LoggingStubs = """
// Extension methods only resolve when their namespace is in scope; the analyzed fixture files
// call logger.LogError(...) without usings, so bring both namespaces in globally.
global using Microsoft.Extensions.Logging;
global using Microsoft.AspNetCore.Http;
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public static class LoggerExtensions
    {
        public static void LogError(this ILogger logger, string message) { }
        public static void LogInformation(this ILogger logger, string message) { }
    }
}
namespace Microsoft.AspNetCore.Http
{
    public interface IHeaderDictionary { }
    public static class HeaderDictionaryExtensions
    {
        public static void Append(this IHeaderDictionary headers, string name, string value) { }
    }
}
""" + HtmlStubs;

    public const string TemplateStubs = """
namespace Scriban
{
    public static class Template { public static Scriban.TemplateStub Parse(string source) => null; }
    public class TemplateStub { }
}
""";
}
