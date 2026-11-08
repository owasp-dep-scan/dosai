using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     IL-mode data-flow parity with the source walker. Each fixture is compiled by Roslyn into
///     an assembly with a portable PDB and no source beside it, so only the IL pass sees it.
///     ASP.NET, EF Core and Blazor types are stubbed in their real namespaces: the IL pass
///     matches them by name exactly as it would against the packages. Assertions are keyed by
///     the method that holds each slice's source.
/// </summary>
public class IlDataFlowParityTests
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-il-parity-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Compiles <paramref name="source" /> into <c>Fixture.dll</c> plus its PDB in <paramref name="directory" />.</summary>
    internal static string EmitAssembly(string directory, string source, OptimizationLevel optimization = OptimizationLevel.Release, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary, string assemblyName = "Fixture", IEnumerable<string>? extraReferences = null)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference)
            .Concat((extraReferences ?? []).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)))
            .ToList();
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), new CSharpParseOptions(LanguageVersion.Preview), path: "/_/Fixture/Fixture.cs")],
            references,
            new CSharpCompilationOptions(outputKind, optimizationLevel: optimization, nullableContextOptions: NullableContextOptions.Enable));
        var dllPath = System.IO.Path.Combine(directory, assemblyName + (outputKind == OutputKind.ConsoleApplication ? ".dll" : ".dll"));
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(System.IO.Path.ChangeExtension(dllPath, ".pdb")))
        {
            var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return dllPath;
    }

    /// <summary>Sink categories of the IL slices, keyed by the method that holds each slice's source node.</summary>
    private static Dictionary<string, HashSet<string>> Analyze(string source, OptimizationLevel optimization = OptimizationLevel.Release, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        using var fixture = new TempDir();
        EmitAssembly(fixture.Path, source, optimization, outputKind);
        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        return SinksBySourceMethod(result);
    }

    internal static Dictionary<string, HashSet<string>> SinksBySourceMethod(DataFlowResult result)
    {
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        var sinks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var slice in result.Slices.Where(slice => slice.Summary?.StartsWith("Assembly IL", StringComparison.Ordinal) == true))
        {
            var method = nodes[slice.SourceId].MethodName ?? string.Empty;
            if (!sinks.TryGetValue(method, out var categories))
            {
                sinks[method] = categories = new HashSet<string>(StringComparer.Ordinal);
            }

            categories.Add(slice.SinkCategory ?? string.Empty);
        }

        return sinks;
    }

    private static bool Reaches(Dictionary<string, HashSet<string>> sinks, string method, string category) =>
        sinks.TryGetValue(method, out var categories) && categories.Contains(category);

    private const string Header = """
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

""";

    private const string Stubs = """

namespace Microsoft.AspNetCore.Html
{
    public class HtmlString { public HtmlString(string? value) { } }
}

namespace Microsoft.AspNetCore.Http
{
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class AsParametersAttribute : Attribute { }
}

namespace Microsoft.AspNetCore.Components
{
    public abstract class ComponentBase { }
    [AttributeUsage(AttributeTargets.Property)] public sealed class SupplyParameterFromQueryAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Property)] public sealed class SupplyParameterFromFormAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Property)] public sealed class ParameterAttribute : Attribute { }
}

namespace Microsoft.AspNetCore.Mvc
{
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)] public sealed class FromHeaderAttribute : Attribute { public FromHeaderAttribute() { } public string? Name { get; set; } }
    [AttributeUsage(AttributeTargets.Property)] public sealed class BindPropertyAttribute : Attribute { }
    public interface IActionResult { }
    public class RedirectResult : IActionResult { public RedirectResult(string url) { } }
    public interface IUrlHelper { bool IsLocalUrl(string? url); }
    public abstract class ControllerBase
    {
        public IUrlHelper Url { get; set; } = null!;
        public RedirectResult Redirect(string url) => new(url);
    }
}

namespace Microsoft.EntityFrameworkCore.Infrastructure
{
    public class DatabaseFacade { }
}

namespace Microsoft.EntityFrameworkCore
{
    public static class RelationalDatabaseFacadeExtensions
    {
        public static int ExecuteSqlRaw(this Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql, params object[] parameters) => 0;
    }
}
""";

    [Fact]
    public void AttributeSources_FromHeaderAsParametersAndPropertyAttributes()
    {
        var sinks = Analyze(Header + """
public record Search(string Term);

public class Api
{
    public void Header([FromHeader(Name = "X-Cmd")] string header) => Process.Start(header);
    public void Parameters([AsParameters] Search search) => Process.Start(search.Term);
    public void Plain(string unbound) => Process.Start(unbound);
}

public class SearchPage : ComponentBase
{
    [SupplyParameterFromQuery] public string? Query { get; set; }
    [SupplyParameterFromForm] public string? Posted { get; set; }
    [Parameter] public string? FromParent { get; set; }
    public void OnQuery() => Process.Start(Query!);
    public void OnPosted() => Process.Start(Posted!);
    public void OnParent() => Process.Start(FromParent!);
}

public class EditModel
{
    [BindProperty] public string? Name { get; set; }
    public void OnPost() => Process.Start(Name!);
}
""" + Stubs);

        Assert.True(Reaches(sinks, "Header", "command"));
        Assert.True(Reaches(sinks, "Parameters", "command"));
        Assert.False(sinks.ContainsKey("Plain"));
        Assert.True(Reaches(sinks, "OnQuery", "command"));
        Assert.True(Reaches(sinks, "OnPosted", "command"));
        Assert.True(Reaches(sinks, "OnPost", "command"));
        // A component parameter comes from the parent component, not the request.
        Assert.False(sinks.ContainsKey("OnParent"));
    }

    [Fact]
    public void TopLevelStatementArgs_AreACliSource()
    {
        using var fixture = new TempDir();
        EmitAssembly(fixture.Path, """
using System.Diagnostics;
Process.Start(args[0]);
""", outputKind: OutputKind.ConsoleApplication);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var source = Assert.Single(result.Nodes, node => node.IsSource && node.Name == "args");
        Assert.Equal("cli", source.Category);
        Assert.Contains(result.Slices, slice => slice.SourceId == source.Id && slice.SinkCategory == "command");
    }

    [Fact]
    public void ObjectInitializersAndSetters_CarryTaintIntoTheWholeObject()
    {
        var sinks = Analyze(Header + """
public class Options
{
    public string Command { get; set; } = "";
    public string Mode { get; set; } = "";
}

public class Job { public string Path { get; set; } = ""; }

public class Runner
{
    private string command = "";
    public void Set(string c) => command = c;
    public void Go() => Process.Start(command);
}

public static class Flows
{
    public static void AutoProperty() { var i = Console.ReadLine()!; var o = new Options { Command = i }; Process.Start(o.Command); }
    public static void SetterThenRead() { var i = Console.ReadLine()!; var o = new Options(); o.Command = i; Process.Start(o.Command); }
    public static void CleanSibling() { var i = Console.ReadLine()!; var o = new Options { Command = i, Mode = "fixed" }; Process.Start(o.Mode); }
    public static void ReassignedClean() { var i = Console.ReadLine()!; var o = new Options { Command = i }; o.Command = "fixed"; Process.Start(o.Command); }
    public static void WholeJob() { var i = Console.ReadLine()!; var job = new Job { Path = i }; Launch(job); }
    public static void SetterThenGo() { var i = Console.ReadLine()!; var r = new Runner(); r.Set(i); r.Go(); }
    private static void Launch(Job job) => Process.Start(job.Path);
}
""" + Stubs);

        Assert.True(Reaches(sinks, "AutoProperty", "command"));
        Assert.True(Reaches(sinks, "SetterThenRead", "command"));
        Assert.True(Reaches(sinks, "WholeJob", "command"));
        Assert.True(Reaches(sinks, "SetterThenGo", "command"));
        Assert.False(sinks.ContainsKey("CleanSibling"));
        Assert.False(sinks.ContainsKey("ReassignedClean"));
    }

    [Fact]
    public void PropertySetterSinks_AndSettersOnTheWholeObject()
    {
        var sinks = Analyze(Header + """
public static class Flows
{
    public static void CommandText(DbConnection connection) { var q = Console.ReadLine()!; var cmd = connection.CreateCommand(); cmd.CommandText = q; }
    public static void CleanSetter(DbConnection connection) { var q = Console.ReadLine()!; var cmd = connection.CreateCommand(); cmd.CommandText = "select 1"; }
    public static void Arguments() { var q = Console.ReadLine()!; var psi = new ProcessStartInfo("ls"); psi.Arguments = q; }
    private static void Prepare(DbCommand command, string text) => command.CommandText = text;
    public static void ThroughHelper(DbConnection connection) { var q = Console.ReadLine()!; Prepare(connection.CreateCommand(), q); }
}
""" + Stubs);

        Assert.True(Reaches(sinks, "CommandText", "sql"));
        Assert.False(sinks.ContainsKey("CleanSetter"));
        Assert.True(Reaches(sinks, "Arguments", "command"));
        Assert.True(Reaches(sinks, "ThroughHelper", "sql"));
    }

    [Fact]
    public void Helpers_ThatReturnASource_TaintTheirCallers()
    {
        var sinks = Analyze(Header + """
public static class Flows
{
    static string SourceViaLocal() { var line = Console.ReadLine(); return line!; }
    static string SourceTwoHops() => SourceViaLocal();
    static bool IsSafe() => Console.ReadLine() == "ok";
    public static void SourceHelper() { Process.Start(SourceViaLocal()); }
    public static void TwoHops() { Process.Start(SourceTwoHops()); }
    public static void DecisionOnly() { Process.Start(IsSafe().ToString()); }
}
""" + Stubs);

        Assert.True(Reaches(sinks, "SourceHelper", "command"));
        Assert.True(Reaches(sinks, "TwoHops", "command"));
        Assert.False(sinks.ContainsKey("DecisionOnly"));
    }

    [Fact]
    public void Encoders_NeutralizeOnlyTheirOwnSinkCategories()
    {
        var sinks = Analyze(Header + """
public static class Flows
{
    public static HtmlString XssEncoded() { var q = Console.ReadLine()!; return new HtmlString(HtmlEncoder.Default.Encode(q)); }
    public static HtmlString XssWebUtility() { var q = Console.ReadLine()!; return new HtmlString(WebUtility.HtmlEncode(q)); }
    public static HtmlString XssRaw() { var q = Console.ReadLine()!; return new HtmlString(q); }
    public static void CommandEncoded() { var q = Console.ReadLine()!; Process.Start(HtmlEncoder.Default.Encode(q)); }
    public static void CommandWebUtility() { var q = Console.ReadLine()!; var encoded = WebUtility.HtmlEncode(q); Process.Start(encoded); }
}
""" + Stubs);

        Assert.False(sinks.ContainsKey("XssEncoded"));
        Assert.False(sinks.ContainsKey("XssWebUtility"));
        Assert.True(Reaches(sinks, "XssRaw", "xss"));
        Assert.True(Reaches(sinks, "CommandEncoded", "command"));
        Assert.True(Reaches(sinks, "CommandWebUtility", "command"));
    }

    [Fact]
    public void ArgumentSpecificSinks_FlagOnlyTheDangerousArgument()
    {
        var sinks = Analyze(Header + """
public static class Flows
{
    public static bool RegexSubject() { var q = Console.ReadLine()!; return Regex.IsMatch(q, "^[a-z]+$"); }
    public static bool RegexPattern() { var q = Console.ReadLine()!; return Regex.IsMatch("abc", q); }
    public static Regex RegexConstructed() { var q = Console.ReadLine()!; return new Regex(q); }
    public static int SqlParameters(DatabaseFacade db) { var q = Console.ReadLine()!; return db.ExecuteSqlRaw("delete from t where id = {0}", q); }
    public static int SqlText(DatabaseFacade db) { var q = Console.ReadLine()!; return db.ExecuteSqlRaw("delete from t where id = " + q); }
}
""" + Stubs);

        Assert.False(Reaches(sinks, "RegexSubject", "redos"));
        Assert.True(Reaches(sinks, "RegexPattern", "redos"));
        Assert.True(Reaches(sinks, "RegexConstructed", "redos"));
        Assert.False(Reaches(sinks, "SqlParameters", "sql"));
        Assert.True(Reaches(sinks, "SqlText", "sql"));
    }

    [Theory]
    [InlineData(OptimizationLevel.Release)]
    [InlineData(OptimizationLevel.Debug)]
    public void EarlyExitGuards_StopTheFlow(OptimizationLevel optimization)
    {
        var sinks = Analyze(Header + """
public class Flows : ControllerBase
{
    public static void ReturnGuard() { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) return; Process.Start(q); }
    public static void ThrowGuard() { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) { throw new ArgumentException("bad"); } Process.Start(q); }
    public static void NoExit() { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) { Debug.WriteLine("odd"); } Process.Start(q); }
    public object EarlyRedirect() { var q = Console.ReadLine()!; if (!Url.IsLocalUrl(q)) return Redirect("/"); return Redirect(q); }
    public static void Parsed() { var q = Console.ReadLine()!; int.TryParse(q, out var n); Process.Start("worker", n.ToString()); }
}
""" + Stubs, optimization);

        Assert.False(sinks.ContainsKey("ReturnGuard"));
        Assert.False(sinks.ContainsKey("ThrowGuard"));
        Assert.True(Reaches(sinks, "NoExit", "command"));
        Assert.False(Reaches(sinks, "EarlyRedirect", "redirect"));
        Assert.False(sinks.ContainsKey("Parsed"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SinkParameters_ResolveThroughTheNuGetCache_OrReportTheFallback(bool packageInCache)
    {
        using var library = new TempDir();
        using var cache = new TempDir();
        using var fixture = new TempDir();
        using var patterns = new TempDir();
        var libraryPath = EmitAssembly(library.Path, "namespace IlSinkLib { public static class Db { public static void Exec(string sql, string arg) { } } }", assemblyName: "IlSinkLib");
        if (packageInCache)
        {
            var packageDirectory = System.IO.Path.Combine(cache.Path, "ilsinklib", "1.2.0", "lib", "net8.0");
            Directory.CreateDirectory(packageDirectory);
            File.Copy(libraryPath, System.IO.Path.Combine(packageDirectory, "IlSinkLib.dll"));
        }

        EmitAssembly(fixture.Path, """
using System;
public static class Flows
{
    public static void Bound() { var q = Console.ReadLine()!; IlSinkLib.Db.Exec("select 1 where id = @id", q); }
    public static void Text() { var q = Console.ReadLine()!; IlSinkLib.Db.Exec("select " + q, "x"); }
}
""", extraReferences: [libraryPath]);
        var patternsPath = System.IO.Path.Combine(patterns.Path, "patterns.json");
        File.WriteAllText(patternsPath, """
{ "sinks": [ { "kind": "Method", "match": "Contains", "pattern": "IlSinkLib.Db.Exec", "category": "sql", "sinkParameters": ["sql"] } ] }
""");

        DataFlowResult result;
        using (FrameworkReferences.OverrideNuGetRootForTesting(cache.Path))
        {
            result = DataFlowAnalyzer.Analyze(fixture.Path, patternsPath);
        }

        var sinks = SinksBySourceMethod(result);
        Assert.True(Reaches(sinks, "Text", "sql"));
        Assert.Equal(!packageInCache, Reaches(sinks, "Bound", "sql"));
        Assert.Equal(!packageInCache, result.Diagnostics.Any(diagnostic => diagnostic.Contains("SinkParameters could not be applied to calls into IlSinkLib", StringComparison.Ordinal)));
    }

    [Fact]
    public void PropertyAttributeSources_FollowInheritanceAndReferencedAssemblies()
    {
        using var fixture = new TempDir();
        using var build = new TempDir();
        var libraryPath = EmitAssembly(build.Path, """
using System;
namespace Microsoft.AspNetCore.Components
{
    [AttributeUsage(AttributeTargets.Property)] public sealed class SupplyParameterFromQueryAttribute : Attribute { }
    public abstract class ComponentBase { }
}
namespace Microsoft.AspNetCore.Mvc
{
    [AttributeUsage(AttributeTargets.Property)] public sealed class BindPropertyAttribute : Attribute { }
}
namespace Shared
{
    public abstract class SearchBase : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.SupplyParameterFromQuery] public string? Query { get; set; }
        [Microsoft.AspNetCore.Mvc.BindProperty] public virtual string? Name { get; set; }
        public string? Plain { get; set; }
    }
}
""", assemblyName: "SharedComponents");
        File.Copy(libraryPath, System.IO.Path.Combine(fixture.Path, "SharedComponents.dll"));
        EmitAssembly(fixture.Path, """
using System.Diagnostics;
public class LocalBase { [Microsoft.AspNetCore.Mvc.BindProperty] public virtual string? Title { get; set; } }
public class LocalDerived : LocalBase
{
    public override string? Title { get; set; }
    public void FromOverride() => Process.Start(Title!);
}
public class SearchPage : Shared.SearchBase
{
    public override string? Name { get; set; }
    public void FromBase() => Process.Start(Query!);
    public void FromReferencedOverride() => Process.Start(Name!);
    public void FromPlain() => Process.Start(Plain!);
}
""", extraReferences: [libraryPath]);

        var sinks = SinksBySourceMethod(DataFlowAnalyzer.Analyze(fixture.Path));

        Assert.True(Reaches(sinks, "FromOverride", "command"));
        Assert.True(Reaches(sinks, "FromBase", "command"));
        Assert.True(Reaches(sinks, "FromReferencedOverride", "command"));
        Assert.False(sinks.ContainsKey("FromPlain"));
    }

    [Fact]
    public void ObjectState_IsKeyedByInstance()
    {
        var sinks = Analyze(Header + """
public class Runner
{
    private string command = "";
    private readonly string fixedCommand;
    private string other = "safe";
    public string Field = "";
    public Runner(string c) { fixedCommand = c; }
    public void Set(string c) => command = c;
    public void Go() => Process.Start(command);
    public void GoFixed() => Process.Start(fixedCommand);
    public void GoOther() => Process.Start(other);
}

public static class Flows
{
    public static void SetterThenGo() { var i = Console.ReadLine()!; var r = new Runner("x"); r.Set(i); r.Go(); }
    public static void ConstructorThenGo() { var i = Console.ReadLine()!; var r = new Runner(i); r.GoFixed(); }
    public static void ReassignedReceiver() { var i = Console.ReadLine()!; var r = new Runner("x"); r.Set(i); r = new Runner("y"); r.Go(); }
    public static void OtherInstance() { var i = Console.ReadLine()!; var a = new Runner("x"); var b = new Runner("y"); a.Set(i); b.Go(); }
    public static void UntaintedMember() { var i = Console.ReadLine()!; var r = new Runner(i); r.GoOther(); }
    public static void PublicField() { var i = Console.ReadLine()!; var a = new Runner("x"); var b = new Runner("y"); a.Field = i; Process.Start(a.Field); Process.Start(b.Field); }
    public static void OtherFieldInstance() { var i = Console.ReadLine()!; var a = new Runner("x"); var b = new Runner("y"); a.Field = i; Process.Start(b.Field); }
}
""" + Stubs);

        Assert.True(Reaches(sinks, "SetterThenGo", "command"));
        Assert.True(Reaches(sinks, "ConstructorThenGo", "command"));
        Assert.True(Reaches(sinks, "PublicField", "command"));
        // A new object in the variable, or another object, does not carry the first one's members.
        Assert.False(sinks.ContainsKey("ReassignedReceiver"));
        Assert.False(sinks.ContainsKey("OtherInstance"));
        Assert.False(sinks.ContainsKey("UntaintedMember"));
        Assert.False(sinks.ContainsKey("OtherFieldInstance"));
    }

    [Fact]
    public void ObjectState_LargeMethodsSinkTheirReceiverMembersToo()
    {
        var sinks = Analyze(Header + """
public class Runner
{
    private string command = "";
    public void Set(string c) => command = c;
    public void Go()
    {
        var total = 0;
        for (var i = 0; i < 10; i++) { total += i * 2; Console.WriteLine(total); Console.WriteLine(i); Console.WriteLine(total - i); }
        for (var i = 0; i < 10; i++) { total -= i * 2; Console.WriteLine(total); Console.WriteLine(i + 1); Console.WriteLine(total - i * 2); }
        for (var i = 0; i < 10; i++) { total += i * 2; Console.WriteLine(total); Console.WriteLine(i + 2); Console.WriteLine(total * i); }
        Process.Start(command);
    }
}

public static class Flows
{
    public static void SetterThenLargeGo() { var i = Console.ReadLine()!; var r = new Runner(); r.Set(i); r.Go(); }
    public static void ReassignedThenLargeGo() { var i = Console.ReadLine()!; var r = new Runner(); r.Set(i); r = new Runner(); r.Go(); }
}
""" + Stubs);

        Assert.True(Reaches(sinks, "SetterThenLargeGo", "command"));
        Assert.False(sinks.ContainsKey("ReassignedThenLargeGo"));
    }

    /// <summary>
    ///     The forwarded-header sources match from IL alone: the rewritten accessors by their
    ///     declaring types, and a literal-name header read by the ldstr feeding the dictionary
    ///     call. The receiver must come from the request side (HttpRequest.get_Headers), so the
    ///     same literal through Response.Headers gives nothing.
    /// </summary>
    private const string ForwardedStubs = """
namespace Microsoft.AspNetCore.HttpOverrides
{
    public static class ForwardedHeadersDefaults
    {
        public static string XForwardedForHeaderName { get; } = "X-Forwarded-For";
        public static string XForwardedHostHeaderName { get; } = "X-Forwarded-Host";
        public static string XForwardedPrefixHeaderName { get; } = "X-Forwarded-Prefix";
    }
}

namespace Microsoft.Extensions.Primitives
{
    public struct StringValues { public override string ToString() => string.Empty; }
}
namespace Microsoft.AspNetCore.Http
{
    public struct HostString { public override string ToString() => string.Empty; }
    public abstract class ConnectionInfo
    {
        public abstract System.Net.IPAddress? RemoteIpAddress { get; }
        public abstract int RemotePort { get; }
    }
    public class DefaultConnectionInfo : ConnectionInfo
    {
        public override System.Net.IPAddress? RemoteIpAddress => null;
        public override int RemotePort => 0;
    }
    public interface IHeaderDictionary
    {
        Microsoft.Extensions.Primitives.StringValues this[string key] { get; }
        bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value);
    }
    public class HeaderDictionary : IHeaderDictionary
    {
        public Microsoft.Extensions.Primitives.StringValues this[string key] => default;
        public bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value) { value = default; return false; }
    }
    public class HttpContext
    {
        public ConnectionInfo Connection { get; } = new DefaultConnectionInfo();
        public HttpRequest Request { get; } = new();
        public HttpResponse Response { get; } = new();
    }
    public class HttpRequest
    {
        public HostString Host { get; } = default;
        public string Scheme { get; } = string.Empty;
        public bool IsHttps { get; }
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
    }
    public class HttpResponse { public IHeaderDictionary Headers { get; } = new HeaderDictionary(); }
}
namespace Microsoft.AspNetCore.Http.Extensions
{
    public static class UriHelper
    {
        public static string GetDisplayUrl(Microsoft.AspNetCore.Http.HttpRequest request) => string.Empty;
    }
}
""" + Stubs;

    [Fact]
    public void ForwardedHeaderSources_WorkFromAssemblyIl()
    {
        using var fixture = new TempDir();
        EmitAssembly(fixture.Path, """
using System;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

public class AppConnection { public IPAddress? RemoteIpAddress { get; } }

public static class Flows
{
    public static void ClientIp(HttpContext ctx) => Process.Start("tool", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
    public static void ClientPort(HttpContext ctx) => Process.Start("tool", ctx.Connection.RemotePort.ToString());
    public static void Host(HttpContext ctx) => Process.Start("tool", ctx.Request.Host.ToString());
    public static void Scheme(HttpContext ctx) => Process.Start("tool", ctx.Request.Scheme);
    public static void DisplayUrl(HttpContext ctx) => Process.Start("tool", UriHelper.GetDisplayUrl(ctx.Request));
    public static void HeaderIndexer(HttpContext ctx) => Process.Start("tool", ctx.Request.Headers["X-Forwarded-For"].ToString());
    public static void HeaderTryGetValue(HttpContext ctx) { ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto); Process.Start("tool", proto.ToString()); }
    public static void ResponseHeader(HttpContext ctx) => Process.Start("tool", ctx.Response.Headers["X-Forwarded-For"].ToString());
    public static void DefaultsHeaderName(HttpContext ctx) => Process.Start("tool", ctx.Request.Headers[Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersDefaults.XForwardedForHeaderName].ToString());
    public static void DefaultsPrefixHeaderName(HttpContext ctx) => Process.Start("tool", ctx.Request.Headers[Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersDefaults.XForwardedPrefixHeaderName].ToString());
    public static void HeaderNonLiteral(HttpContext ctx, string name) => Process.Start("tool", ctx.Request.Headers[name].ToString());
    public static void AppClientIp(AppConnection conn) => Process.Start("tool", conn.RemoteIpAddress?.ToString() ?? "");
    public static void FromHeaderForwarded([Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Forwarded-For")] string ip) => Process.Start("tool", ip);
}
""" + ForwardedStubs);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        var categories = result.Nodes
            .Where(node => node.IsSource)
            .GroupBy(node => node.MethodName ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.Select(node => (node.Category, node.Properties.GetValueOrDefault("header"))).ToList(), StringComparer.Ordinal);
        bool ProxyDerived(string method) => categories.TryGetValue(method, out var entries) && entries.Any(entry => entry.Category == "proxy-derived");

        Assert.True(ProxyDerived("ClientIp"));
        Assert.True(ProxyDerived("ClientPort"));
        Assert.True(ProxyDerived("Host"));
        Assert.True(ProxyDerived("Scheme"));
        Assert.True(ProxyDerived("DisplayUrl"));
        Assert.True(ProxyDerived("HeaderIndexer"));
        Assert.True(ProxyDerived("HeaderTryGetValue"));
        Assert.True(ProxyDerived("FromHeaderForwarded"));
        Assert.True(ProxyDerived("DefaultsHeaderName"));
        Assert.False(ProxyDerived("DefaultsPrefixHeaderName"));
        Assert.Equal("x-forwarded-for", result.Nodes.Single(node => node.IsSource && node.MethodName == "HeaderIndexer" && node.Properties.ContainsKey("header")).Properties.GetValueOrDefault("header"));
        Assert.Equal("x-forwarded-for", result.Nodes.Single(node => node.IsSource && node.MethodName == "FromHeaderForwarded" && node.Properties.ContainsKey("header")).Properties.GetValueOrDefault("header"));

        // The same members on an application class, the same literal through Response.Headers,
        // and a non-literal header name are not forwarded-header reads.
        Assert.False(ProxyDerived("AppClientIp"));
        Assert.False(ProxyDerived("ResponseHeader"));
        Assert.False(ProxyDerived("HeaderNonLiteral"));

        var nodesById = result.Nodes.ToDictionary(node => node.Id);
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "proxy-derived", SinkCategory: "command" } &&
                                                nodesById.TryGetValue(slice.SourceId, out var source) && source.MethodName == "ClientIp");
    }
}
