using System.Text.Json;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     MCP root confinement (<c>--mcp-root</c>): every path-valued tool argument is checked with
///     symbolic links resolved, a confined tree walk cannot follow a link out of the root, and a
///     pattern file that is not valid JSON is answered with a generic message that never quotes
///     its content back over JSON-RPC.
/// </summary>
public class McpConfinementTests
{
    [Fact]
    public void McpToolCall_PatternsFileOutsideTheRoot_IsRefused()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        WriteSourceFixture(root.Path, "InsidePatterns");
        var patterns = Path.Combine(outside.Path, "patterns.json");
        File.WriteAllText(patterns, """{"sources":[],"sinks":[],"passthroughs":[],"sanitizers":[]}""");

        var response = RunServer(root.Path, null, root.Path, ToolCallRequest(1, "dosai.dataflows", new Dictionary<string, string>
        {
            ["path"] = root.Path,
            ["patterns"] = patterns
        }));

        Assert.Contains("Patterns file is outside the --mcp-root confinement", response, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void McpToolCall_PathThroughSymlinkOutsideTheRoot_IsRefused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        WriteSourceFixture(root.Path, "InsideLink");
        WriteSourceFixture(outside.Path, "OutsideLink");
        var link = Path.Combine(root.Path, "linked");
        Directory.CreateSymbolicLink(link, outside.Path);

        var response = RunServer(root.Path, null, root.Path, ToolCallRequest(2, "dosai.methods", new Dictionary<string, string>
        {
            ["path"] = link
        }));

        Assert.Contains("\"error\"", response, StringComparison.Ordinal);
        Assert.Contains("Path is outside the --mcp-root confinement", response, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void McpToolCall_PathThroughSymlinkInsideTheRoot_IsAccepted()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var root = new TemporaryDirectory();
        var real = Path.Combine(root.Path, "real");
        Directory.CreateDirectory(real);
        WriteSourceFixture(real, "Aliased");
        var link = Path.Combine(root.Path, "alias");
        Directory.CreateSymbolicLink(link, real);

        var response = RunServer(root.Path, null, root.Path, ToolCallRequest(3, "dosai.methods", new Dictionary<string, string>
        {
            ["path"] = link
        }));

        Assert.Contains("\"result\"", response, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", response, StringComparison.Ordinal);
        Assert.Contains("Aliased", response, StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolCall_InvalidPatternFileJson_ReturnsGenericMessage()
    {
        using var root = new TemporaryDirectory();
        WriteSourceFixture(root.Path, "InsidePatternJson");
        var patterns = Path.Combine(root.Path, "patterns.json");
        File.WriteAllText(patterns, "<DosaiSecretMarker>");

        var response = RunServer(root.Path, null, root.Path, ToolCallRequest(4, "dosai.dataflows", new Dictionary<string, string>
        {
            ["path"] = root.Path,
            ["patterns"] = patterns
        }));

        Assert.Contains("Pattern file is not valid JSON", response, StringComparison.Ordinal);
        Assert.DoesNotContain("DosaiSecretMarker", response, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", response, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void McpToolCall_ConfinedTreeWalk_DoesNotFollowLinksOutOfTheRoot()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        WriteSourceFixture(root.Path, "InsideWalk");
        WriteSourceFixture(outside.Path, "OutsideWalk");
        var link = Path.Combine(root.Path, "linked");
        Directory.CreateSymbolicLink(link, outside.Path);

        var confined = RunServer(root.Path, null, root.Path, ToolCallRequest(5, "dosai.methods", new Dictionary<string, string>
        {
            ["path"] = root.Path
        }));
        Assert.Contains("InsideWalk", confined, StringComparison.Ordinal);
        Assert.DoesNotContain("OutsideWalk", confined, StringComparison.Ordinal);

        // Without confinement, linked directories are a normal repository layout and the walk
        // keeps following them.
        var unconfined = RunServer(root.Path, null, null, ToolCallRequest(6, "dosai.methods", new Dictionary<string, string>
        {
            ["path"] = root.Path
        }));
        Assert.Contains("InsideWalk", unconfined, StringComparison.Ordinal);
        Assert.Contains("OutsideWalk", unconfined, StringComparison.Ordinal);
    }

    [Fact]
    public void McpRun_PatternsFileDefaultOutsideTheRoot_IsRefusedAtStartup()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var patterns = Path.Combine(outside.Path, "patterns.json");
        File.WriteAllText(patterns, """{"sources":[],"sinks":[],"passthroughs":[],"sanitizers":[]}""");

        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        var exitCode = McpServer.Run(root.Path, patterns, null, root.Path, input, output);

        Assert.Equal(2, exitCode);
        Assert.Contains("--patterns must fall under --mcp-root", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IsUnderRoot_PlainPaths_CompareByRootPrefix()
    {
        using var root = new TemporaryDirectory();
        Assert.True(McpServer.IsUnderRoot(root.Path, root.Path));
        Assert.True(McpServer.IsUnderRoot(Path.Combine(root.Path, "sub", "Program.cs"), root.Path));
        Assert.False(McpServer.IsUnderRoot(Path.Combine(Path.GetDirectoryName(root.Path)!, "sibling"), root.Path));
    }

    [SkippableFact]
    public void IsUnderRoot_SymlinkOutsideTheRoot_IsRefused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var link = Path.Combine(root.Path, "linked");
        Directory.CreateSymbolicLink(link, outside.Path);

        Assert.False(McpServer.IsUnderRoot(link, root.Path));
    }

    [SkippableFact]
    public void IsUnderRoot_SymlinkInsideTheRoot_IsAccepted()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var root = new TemporaryDirectory();
        var real = Path.Combine(root.Path, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root.Path, "alias");
        Directory.CreateSymbolicLink(link, real);

        Assert.True(McpServer.IsUnderRoot(link, root.Path));
        Assert.True(McpServer.IsUnderRoot(Path.Combine(link, "Program.cs"), root.Path));
    }

    [SkippableFact]
    public void IsUnderRoot_MissingPathBehindSymlinkOutOfTheRoot_IsRefused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory links needs elevation on Windows.");

        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var link = Path.Combine(root.Path, "linked");
        Directory.CreateSymbolicLink(link, outside.Path);

        // The path does not exist yet; the link component still has to resolve inside the root.
        Assert.False(McpServer.IsUnderRoot(Path.Combine(link, "new", "Program.cs"), root.Path));
    }

    // The path-argument list and the tool schema must stay in step, in both directions: every
    // registered name is a declared schema property, and every schema property whose
    // description says it is a file or directory is registered (that is how a future path
    // argument that ships without joining McpServer.PathArgumentNames fails here). Each
    // registered name is then also refused when its value sits outside the root, on a call
    // that would otherwise run, which pins the validation entry point itself.
    [Fact]
    public void McpPathArgumentNames_MatchTheToolSchemaAndEachIsRefusedOutsideTheRoot()
    {
        var properties = ReadToolSchemaProperties(out var failure);
        Assert.Null(failure);
        Assert.NotEmpty(properties);

        foreach (var name in McpServer.PathArgumentNames)
        {
            Assert.True(properties.ContainsKey(name), $"Path argument '{name}' is missing from the tool schema.");
        }

        foreach (var (name, description) in properties)
        {
            var declaresPath = description.Contains("file", StringComparison.OrdinalIgnoreCase)
                || description.Contains("directory", StringComparison.OrdinalIgnoreCase);
            Assert.True(!declaresPath || McpServer.PathArgumentNames.Contains(name),
                $"Schema property '{name}' describes a file or directory but is not in McpServer.PathArgumentNames, so confinement would not check it.");
        }

        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        foreach (var name in McpServer.PathArgumentNames)
        {
            var response = RunServer(root.Path, null, root.Path, ToolCallRequest(8, "dosai.dataflows", new Dictionary<string, string>
            {
                [name] = Path.Combine(outside.Path, name + ".json")
            }));
            Assert.Contains("outside the --mcp-root confinement", response, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, string> ReadToolSchemaProperties(out string? failure)
    {
        failure = null;
        using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n");
        using var output = new StringWriter();
        McpServer.Run(input: input, output: output);
        var properties = new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(output.ToString());
            foreach (var tool in document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
            {
                foreach (var property in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
                {
                    properties[property.Name] = property.Value.GetProperty("description").GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException)
        {
            failure = $"The tools/list schema was not readable: {ex.Message}";
        }
        return properties;
    }

    private static string ToolCallRequest(int id, string tool, object arguments)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = tool, arguments }
        });
    }

    private static string RunServer(string? defaultPath, string? patternsPath, string? rootPath, string request)
    {
        using var input = new StringReader(request + "\n");
        using var output = new StringWriter();
        McpServer.Run(defaultPath, patternsPath, null, rootPath, input, output);
        return output.ToString();
    }

    private static void WriteSourceFixture(string directory, string className)
    {
        File.WriteAllText(Path.Combine(directory, className + ".cs"), $$"""
public static class {{className}}
{
    public static string Run(string value) => value;
}
""");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-mcp-confine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                // Remove a directory link explicitly first: a recursive delete follows it on
                // some platforms and would take the linked tree with it.
                var link = System.IO.Path.Combine(Path, "linked");
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(link);
                }
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
