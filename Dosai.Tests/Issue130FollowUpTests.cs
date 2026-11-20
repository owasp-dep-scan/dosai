using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #130 follow-ups: initializer calls are attributed only to the constructors that
///     run the initializer (not to ones that chain with <c>: this(...)</c>), and the Visual
///     Basic initializers that stay unrecorded are counted once each.
/// </summary>
public class Issue130FollowUpTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue130b-" + Guid.NewGuid().ToString("N"));

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

    private static MethodsSlice Read(string directory)
    {
        var json = Depscan.Dosai.GetMethods(directory);
        return JsonSerializer.Deserialize<MethodsSlice>(json, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    }

    private static List<string> MakeCallers(string source)
    {
        using var fixture = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Repro.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Types.cs"), source);
        return Read(fixture.Path).MethodCalls!
            .Where(call => call.CalledMethod?.Contains("Make", StringComparison.Ordinal) == true)
            .Select(call => call.SourceId ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void ChainedConstructors_DoNotRunTheInitializer()
    {
        var callers = MakeCallers("""
namespace Ns;

public class Chained
{
    int _a = Make();

    public Chained() : this(1) { }

    public Chained(int x) { }

    public Chained(string s) : base() { }

    static int Make() => 1;
}
""");
        Assert.Equal(["Ns.Chained..ctor(int)", "Ns.Chained..ctor(string)"], callers.Where(id => id.Contains("..ctor", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void PrimaryConstructorTypes_RunTheInitializerOnlyInThePrimary()
    {
        var callers = MakeCallers("""
namespace Ns;

public class Primary(int seed)
{
    int _p = Make();

    public Primary() : this(0) { }

    static int Make() => 1;
}

public record Rec(int X)
{
    int Y { get; } = Make2();

    Rec(string s) : this(s.Length) { }

    static int Make2() => 1;
}
""");
        Assert.Equal(["Ns.Primary..ctor(int)", "Ns.Rec..ctor(int)"], callers.Where(id => id.Contains("..ctor", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void StructWithADeclaredParameterlessConstructor_RunsTheInitializerThereOnly()
    {
        var callers = MakeCallers("""
namespace Ns;

public struct Pair
{
    int _a = Make();

    public Pair() { }

    public Pair(int x) : this() { }

    static int Make() => 1;
}
""");
        Assert.Equal(["Ns.Pair..ctor()"], callers.Where(id => id.Contains("..ctor", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void StructChainingToTheImplicitParameterlessConstructor_KeepsTheInitializer()
    {
        var callers = MakeCallers("""
namespace Ns;

public struct Quad
{
    int _a = Make();

    public Quad(int x) : this() { }

    static int Make() => 1;
}
""");
        Assert.Equal(["Ns.Quad..ctor(int)"], callers.Where(id => id.Contains("..ctor", StringComparison.Ordinal)).ToList());
    }

    [Theory]
    [InlineData("Private Shared ReadOnly StaticField As String = Integer.Parse(\"7\")", 1)]
    [InlineData("Private Builder As New System.Text.StringBuilder()", 1)]
    [InlineData("Public Property Name As String = Integer.Parse(\"8\")", 1)]
    [InlineData("Public Property Created As New System.Text.StringBuilder()", 1)]
    [InlineData("Private Shared ReadOnly Chained As String = Environment.GetEnvironmentVariable(\"A\").Trim()", 2)]
    public void VbInitializers_AreCountedOncePerSite_AsNewAndPropertyInitializersIncluded(string member, int expected)
    {
        using var fixture = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Repro.vbproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Settings.vb"), $"""
Imports System

Namespace Repro

    Public Class Settings

        {member}

    End Class

End Namespace
""");
        var diagnostic = Assert.Single(Read(fixture.Path).Diagnostics!, text => text.Contains("Visual Basic field and property initializers", StringComparison.Ordinal));
        Assert.True(diagnostic.StartsWith($"{expected} call site(s)", StringComparison.Ordinal), diagnostic);
    }
}
