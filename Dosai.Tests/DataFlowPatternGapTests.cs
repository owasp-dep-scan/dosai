using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Source-mode pattern gaps: property setters and conversion operators as sinks,
///     category-scoped sanitizers (an HTML encoder does not stop command injection),
///     argument-specific sinks (the pattern of a Regex call, the SQL text of ExecuteSqlRaw),
///     the sinks and sources the default catalog lacked, validators whose out value is clean,
///     and early-exit validation guards. ASP.NET, EF Core and scripting types are stubbed in
///     their real namespaces, so the patterns bind exactly as they would against the packages.
/// </summary>
public class DataFlowPatternGapTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-dataflow-patterns-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>The sink categories of each method's slices, plus the method's sanitized-flow count.</summary>
    private static (Dictionary<string, HashSet<string>> Sinks, Dictionary<string, int> Sanitized) Analyze(string source)
    {
        using var fixture = new TempDir();
        // The fixture's own types live in the global namespace: the stubs need block namespaces,
        // which a file-scoped namespace would forbid.
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Fixture.cs"), Header + source + Stubs);
        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        var sinks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var slice in result.Slices)
        {
            var method = nodes[slice.SinkId].MethodName ?? string.Empty;
            if (!sinks.TryGetValue(method, out var categories))
            {
                sinks[method] = categories = new HashSet<string>(StringComparer.Ordinal);
            }

            categories.Add(slice.SinkCategory ?? string.Empty);
        }

        var sanitized = result.SanitizedFlows
            .Where(flow => flow.SourceIds.Count > 0 && nodes.ContainsKey(flow.SourceIds[0]))
            .GroupBy(flow => nodes[flow.SourceIds[0]].MethodName ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return (sinks, sanitized);
    }

    private static bool Reaches(Dictionary<string, HashSet<string>> sinks, string method, string category) =>
        sinks.TryGetValue(method, out var categories) && categories.Contains(category);

    private const string Header = """
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using System.Linq.Dynamic.Core;

""";

    private const string Stubs = """

namespace Microsoft.AspNetCore.Html
{
    public class HtmlString { public HtmlString(string? value) { } }
    public interface IHtmlContentBuilder { }
    public static class HtmlContentBuilderExtensions { public static IHtmlContentBuilder AppendHtml(this IHtmlContentBuilder builder, string encoded) => builder; }
}

namespace Microsoft.AspNetCore.Components
{
    public readonly struct MarkupString
    {
        public MarkupString(string value) { }
        public static explicit operator MarkupString(string value) => new(value);
    }
}

namespace Microsoft.AspNetCore.Mvc
{
    public interface IActionResult { }
    public class RedirectResult : IActionResult { public RedirectResult(string url) { } }
    public interface IUrlHelper { bool IsLocalUrl(string? url); }
    public abstract class ControllerBase
    {
        public IUrlHelper Url { get; set; } = null!;
        public RedirectResult Redirect(string url) => new(url);
        public IActionResult LocalRedirect(string localUrl) => null!;
        public IActionResult RedirectToAction(string actionName) => null!;
        public IActionResult PhysicalFile(string physicalPath, string contentType) => null!;
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
        public static System.Linq.IQueryable<T> SqlQueryRaw<T>(this Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql, params object[] parameters) => null!;
    }
}

namespace Microsoft.CodeAnalysis.CSharp.Scripting
{
    public static class CSharpScript { public static System.Threading.Tasks.Task<object> EvaluateAsync(string code, object? options = null) => null!; }
}

namespace System.Linq.Dynamic.Core
{
    public static class DynamicQueryableExtensions { public static System.Linq.IQueryable Where(this System.Linq.IQueryable source, string predicate, params object?[] args) => source; }
}
""";

    [Fact]
    public void PropertySettersAndConversionOperators_AreSinks()
    {
        var (sinks, _) = Analyze("""
public class Page : ControllerBase
{
    public void CommandText(DbConnection connection) { var q = Console.ReadLine()!; var cmd = connection.CreateCommand(); cmd.CommandText = q; }
    public void Arguments() { var q = Console.ReadLine()!; var psi = new ProcessStartInfo("ls"); psi.Arguments = q; }
    public void RequestUri() { var q = Console.ReadLine()!; var message = new HttpRequestMessage(); message.RequestUri = new Uri(q); }
    public void CleanSetter(DbConnection connection) { var q = Console.ReadLine()!; var cmd = connection.CreateCommand(); cmd.CommandText = "select 1"; }
    public MarkupString MarkupCast() { var q = Console.ReadLine()!; return (MarkupString)q; }
}
""");

        Assert.True(Reaches(sinks, "CommandText", "sql"));
        Assert.True(Reaches(sinks, "Arguments", "command"));
        Assert.True(Reaches(sinks, "RequestUri", "network"));
        Assert.False(sinks.ContainsKey("CleanSetter"));
        Assert.True(Reaches(sinks, "MarkupCast", "xss"));
    }

    [Fact]
    public void Encoders_NeutralizeOnlyTheirOwnSinkCategories()
    {
        var (sinks, sanitized) = Analyze("""
public static class Flows
{
    public static HtmlString XssEncoded() { var q = Console.ReadLine()!; return new HtmlString(HtmlEncoder.Default.Encode(q)); }
    public static HtmlString XssWebUtility() { var q = Console.ReadLine()!; return new HtmlString(WebUtility.HtmlEncode(q)); }
    public static HtmlString XssRaw() { var q = Console.ReadLine()!; return new HtmlString(q); }
    public static HtmlString XssHalfEncoded() { var q = Console.ReadLine()!; var r = Console.ReadLine()!; return new HtmlString(HtmlEncoder.Default.Encode(q) + r); }
    public static void CommandEncoded() { var q = Console.ReadLine()!; Process.Start(HtmlEncoder.Default.Encode(q)); }
    public static void CommandWebUtility() { var q = Console.ReadLine()!; var encoded = WebUtility.HtmlEncode(q); Process.Start(encoded); }
    public static void FullPath() { var q = Console.ReadLine()!; File.ReadAllText(Path.GetFullPath(q)); }
}
""");

        Assert.False(sinks.ContainsKey("XssEncoded"));
        Assert.False(sinks.ContainsKey("XssWebUtility"));
        Assert.True(sanitized.GetValueOrDefault("XssEncoded") > 0, "the neutralized flow is negative evidence");
        Assert.True(Reaches(sinks, "XssRaw", "xss"));
        Assert.True(Reaches(sinks, "XssHalfEncoded", "xss"));
        Assert.True(Reaches(sinks, "CommandEncoded", "command"));
        Assert.True(Reaches(sinks, "CommandWebUtility", "command"));
        Assert.True(Reaches(sinks, "FullPath", "file"));
    }

    [Fact]
    public void ArgumentSpecificSinks_FlagOnlyTheDangerousArgument()
    {
        var (sinks, _) = Analyze("""
public static class Flows
{
    public static bool RegexSubject() { var q = Console.ReadLine()!; return Regex.IsMatch(q, "^[a-z]+$"); }
    public static bool RegexPattern() { var q = Console.ReadLine()!; return Regex.IsMatch("abc", q); }
    public static string RegexReplacePattern() { var q = Console.ReadLine()!; return Regex.Replace("abc", q, ""); }
    public static string[] RegexSplitSubject() { var q = Console.ReadLine()!; return Regex.Split(q, ","); }
    public static int RegexCountPattern() { var q = Console.ReadLine()!; return Regex.Count("abc", q); }
    public static Regex RegexEscaped() { var q = Console.ReadLine()!; return new Regex(Regex.Escape(q)); }
    public static Regex RegexConstructed() { var q = Console.ReadLine()!; return new Regex(q); }
    public static int SqlParameters(DatabaseFacade db) { var q = Console.ReadLine()!; return db.ExecuteSqlRaw("delete from t where id = {0}", q); }
    public static int SqlText(DatabaseFacade db) { var q = Console.ReadLine()!; return db.ExecuteSqlRaw("delete from t where id = " + q); }
    public static object SqlQueryRawText(DatabaseFacade db) { var q = Console.ReadLine()!; return db.SqlQueryRaw<int>("select " + q).ToList(); }
    public static object SqlQueryRawParameters(DatabaseFacade db) { var q = Console.ReadLine()!; return db.SqlQueryRaw<int>("select {0}", q).ToList(); }
    public static object DataSource(DbDataSource source) { var q = Console.ReadLine()!; return source.CreateCommand(q); }
    public static object DynamicLinqArgs(IQueryable rows) { var q = Console.ReadLine()!; return rows.Where("Name == @0", q); }
    public static object DynamicLinqPredicate(IQueryable rows) { var q = Console.ReadLine()!; return rows.Where(q); }
    public static object Script() { var q = Console.ReadLine()!; return CSharpScript.EvaluateAsync(q); }
}
""");

        Assert.False(Reaches(sinks, "RegexSubject", "redos"));
        Assert.True(Reaches(sinks, "RegexPattern", "redos"));
        Assert.True(Reaches(sinks, "RegexReplacePattern", "redos"));
        Assert.False(Reaches(sinks, "RegexSplitSubject", "redos"));
        Assert.True(Reaches(sinks, "RegexCountPattern", "redos"));
        Assert.False(Reaches(sinks, "RegexEscaped", "redos"));
        Assert.True(Reaches(sinks, "RegexConstructed", "redos"));
        Assert.False(Reaches(sinks, "SqlParameters", "sql"));
        Assert.True(Reaches(sinks, "SqlText", "sql"));
        Assert.True(Reaches(sinks, "SqlQueryRawText", "sql"));
        Assert.False(Reaches(sinks, "SqlQueryRawParameters", "sql"));
        Assert.True(Reaches(sinks, "DataSource", "sql"));
        Assert.False(Reaches(sinks, "DynamicLinqArgs", "code-injection"));
        Assert.True(Reaches(sinks, "DynamicLinqPredicate", "code-injection"));
        Assert.True(Reaches(sinks, "Script", "code-injection"));
    }

    [Fact]
    public void NetworkFileArchiveReflectionAndMarkupSinks_AreReported()
    {
        var (sinks, _) = Analyze("""
#pragma warning disable SYSLIB0014
public class Flows : ControllerBase
{
    public string WebClientDownload() { var q = Console.ReadLine()!; return new WebClient().DownloadString(q); }
    public object WebRequestCreate() { var q = Console.ReadLine()!; return WebRequest.Create(q); }
    public object JsonGet(HttpClient client) { var q = Console.ReadLine()!; return client.GetFromJsonAsync<string>(q); }
    public object JsonPostBody(HttpClient client) { var q = Console.ReadLine()!; return client.PostAsJsonAsync("https://api.example/items", q); }
    public object RequestMessage() { var q = Console.ReadLine()!; return new HttpRequestMessage(HttpMethod.Get, q); }
    public object FileInfoPath() { var q = Console.ReadLine()!; return new FileInfo(q); }
    public object DirectoryInfoPath() { var q = Console.ReadLine()!; return new DirectoryInfo(q); }
    public object ReaderPath() { var q = Console.ReadLine()!; return new StreamReader(q); }
    public object WriterPath() { var q = Console.ReadLine()!; return new StreamWriter(q); }
    public void ZipTarget() { var q = Console.ReadLine()!; ZipFile.ExtractToDirectory("upload.zip", q); }
    public void ZipSlip(string destination)
    {
        using var archive = ZipFile.OpenRead("upload.zip");
        foreach (var entry in archive.Entries)
        {
            entry.ExtractToFile(Path.Combine(destination, entry.FullName));
        }
    }
    public object PhysicalPath() { var q = Console.ReadLine()!; return PhysicalFile(q, "text/plain"); }
    public object ActivatorType() { var q = Console.ReadLine()!; return Activator.CreateInstance("Plugins", q)!; }
    public object LoadContext() { var q = Console.ReadLine()!; return AssemblyLoadContext.Default.LoadFromAssemblyPath(q); }
    public object AssemblyType() { var q = Console.ReadLine()!; return typeof(Flows).Assembly.GetType(q)!; }
    public object Html() { var q = Console.ReadLine()!; return new HtmlString(q); }
    public object AppendHtml(IHtmlContentBuilder builder) { var q = Console.ReadLine()!; return builder.AppendHtml(q); }
}
""");

        Assert.True(Reaches(sinks, "WebClientDownload", "network"));
        Assert.True(Reaches(sinks, "WebRequestCreate", "network"));
        Assert.True(Reaches(sinks, "JsonGet", "network"));
        Assert.False(Reaches(sinks, "JsonPostBody", "network"));
        Assert.True(Reaches(sinks, "RequestMessage", "network"));
        Assert.True(Reaches(sinks, "FileInfoPath", "file"));
        Assert.True(Reaches(sinks, "DirectoryInfoPath", "file"));
        Assert.True(Reaches(sinks, "ReaderPath", "file"));
        Assert.True(Reaches(sinks, "WriterPath", "file"));
        Assert.True(Reaches(sinks, "ZipTarget", "file"));
        Assert.True(Reaches(sinks, "ZipSlip", "file"));
        Assert.True(Reaches(sinks, "PhysicalPath", "file"));
        Assert.True(Reaches(sinks, "ActivatorType", "reflection"));
        Assert.True(Reaches(sinks, "LoadContext", "reflection"));
        Assert.True(Reaches(sinks, "AssemblyType", "reflection"));
        Assert.True(Reaches(sinks, "Html", "xss"));
        Assert.True(Reaches(sinks, "AppendHtml", "xss"));
    }

    [Fact]
    public void Redirects_LocalRedirectAndRouteRedirectsAreSafe()
    {
        var (sinks, _) = Analyze("""
public class Flows : ControllerBase
{
    public object Open() { var q = Console.ReadLine()!; return Redirect(q); }
    public object Local() { var q = Console.ReadLine()!; return LocalRedirect(q); }
    public object ToAction() { var q = Console.ReadLine()!; return RedirectToAction(q); }
    public object Result() { var q = Console.ReadLine()!; return new RedirectResult(q); }
    public object Checked() { var q = Console.ReadLine()!; if (Url.IsLocalUrl(q)) { return Redirect(q); } return Redirect("/"); }
    public object EarlyExit() { var q = Console.ReadLine()!; if (!Url.IsLocalUrl(q)) return Redirect("/"); return Redirect(q); }
}
""");

        Assert.True(Reaches(sinks, "Open", "redirect"));
        Assert.False(sinks.ContainsKey("Local"));
        Assert.False(sinks.ContainsKey("ToAction"));
        Assert.True(Reaches(sinks, "Result", "redirect"));
        Assert.False(sinks.ContainsKey("Checked"));
        Assert.False(sinks.ContainsKey("EarlyExit"));
    }

    [Fact]
    public void ParsedValuesAndEarlyExitGuards_StopTheFlow()
    {
        var (sinks, sanitized) = Analyze("""
public static class Flows
{
    public static void Parsed() { var q = Console.ReadLine()!; var n = int.Parse(q); Process.Start("worker", n.ToString()); }
    public static void Converted() { var q = Console.ReadLine()!; var n = Convert.ToInt64(q); Process.Start("worker", n.ToString()); }
    public static void TryParsedOut() { var q = Console.ReadLine()!; int.TryParse(q, out var n); Process.Start("worker", n.ToString()); }
    public static void TryParsedGuard() { var q = Console.ReadLine()!; if (Guid.TryParse(q, out var id)) { Process.Start("worker", id.ToString()); } }
    public static void ReturnGuard() { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) return; Process.Start(q); }
    public static void ThrowGuard() { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) { throw new ArgumentException("bad"); } Process.Start(q); }
    public static void ContinueGuard(string[] items) { foreach (var item in items) { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) continue; Process.Start(q); } }
    public static void NoExit() { var q = Console.ReadLine()!; if (!Regex.IsMatch(q, "^[a-z]+$")) { Debug.WriteLine("odd"); } Process.Start(q); }
    public static void GuardEndsWithLoop(string[] items) { var q = Console.ReadLine()!; foreach (var item in items) { if (!Regex.IsMatch(q, "^[a-z]+$")) continue; Process.Start(item); } Process.Start(q); }
}
""");

        Assert.False(sinks.ContainsKey("Parsed"));
        Assert.False(sinks.ContainsKey("Converted"));
        Assert.False(sinks.ContainsKey("TryParsedOut"));
        Assert.True(sanitized.GetValueOrDefault("TryParsedOut") > 0);
        Assert.False(sinks.ContainsKey("TryParsedGuard"));
        Assert.False(sinks.ContainsKey("ReturnGuard"));
        Assert.False(sinks.ContainsKey("ThrowGuard"));
        Assert.False(sinks.ContainsKey("ContinueGuard"));
        Assert.True(Reaches(sinks, "NoExit", "command"));
        Assert.True(Reaches(sinks, "GuardEndsWithLoop", "command"));
    }

    [Fact]
    public void SetterSinksReachedThroughHelpers_AreSummarized()
    {
        var (sinks, _) = Analyze("""
public static class Flows
{
    private static void Prepare(DbCommand command, string text) => command.CommandText = text;
    public static void ThroughHelper(DbConnection connection) { var q = Console.ReadLine()!; Prepare(connection.CreateCommand(), q); }
}
""");

        Assert.True(Reaches(sinks, "ThroughHelper", "sql"));
    }
}
