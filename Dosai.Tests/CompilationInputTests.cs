using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Dosai.Tests;

// What goes into the Roslyn compilation of analyzed source: framework references (issue #67),
// which files (issue #69), which preprocessor symbols each file parses with (nearest project),
// and how the compilation's own errors are reported.
public class CompilationInputTests
{
    private static MethodsSlice ReadMethods(string path)
    {
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice!;
    }

    private const string ReproGreeter = """
namespace Repro;

public class Greeter
{
    public string Greet(string name) => string.Concat("Hello, ", name);
}

public static class Program
{
    public static void Main()
    {
        var greeter = new Greeter();
        System.Console.WriteLine(greeter.Greet("world"));
    }
}
""";

    // ----- Framework references (issue #67) -----

    // The self-contained single-file build references framework assemblies by name from its
    // bundle; the names are embedded at build time from the reference pack, so they must cover
    // the surface an analyzed project binds against.
    [Fact]
    public void FrameworkReferences_EmbeddedNames_CoverTheReferencePack()
    {
        var names = FrameworkReferences.EmbeddedFrameworkAssemblyNames();

        Assert.True(names.Count > 100, $"only {names.Count} framework assembly names embedded");
        Assert.Contains("System.Runtime", names);
        Assert.Contains("System.Console", names);
        Assert.Contains("System.Net.Http", names);
        Assert.Contains("netstandard", names);
        Assert.Contains("System.Private.CoreLib", names);
    }

    // References built from loaded assemblies' in-memory metadata - the bundled-runtime path -
    // bind framework calls exactly like file references do, including types the runtime's
    // facades forward to its System.Private.* implementation assemblies (Uri, XmlDocument,
    // XDocument, DataContractSerializer), and reference the shared framework only. Exercised
    // in-process here: the metadata read is the same for a bundled and a file-loaded assembly.
    [Fact]
    public void FrameworkReferences_FromLoadedAssemblies_BindsFrameworkCalls()
    {
        var warnings = new List<string>();
        var names = FrameworkReferences.EmbeddedFrameworkAssemblyNames();
        var references = FrameworkReferences.FromLoadedAssemblies(names, warnings);

        Assert.Empty(warnings);
        var referenced = references.Select(reference => Path.GetFileNameWithoutExtension(reference.Key)).ToList();
        Assert.All(referenced, name => Assert.True(names.Contains(name) || name.StartsWith("System.Private.", StringComparison.Ordinal), name));
        Assert.Contains("System.Private.Uri", referenced);
        Assert.Contains("System.Private.Xml", referenced);
        var tree = CSharpSyntaxTree.ParseText("""
class C
{
    void M()
    {
        System.Console.WriteLine(string.Concat("a", "b"));
        var items = new System.Collections.Generic.List<int> { 1 };
        _ = System.Linq.Enumerable.Count(items);
        using var client = new System.Net.Http.HttpClient();
        System.Console.WriteLine(new System.Uri("https://example.com").Host);
        new System.Xml.XmlDocument().LoadXml("<a/>");
        _ = System.Xml.Linq.XDocument.Parse("<a/>").Root;
        _ = new System.Runtime.Serialization.DataContractSerializer(typeof(string));
    }
}
""");
        var compilation = CSharpCompilation.Create("Bundled", [tree], references.Select(reference => (MetadataReference)reference.Reference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    // A non-bundled host (this test host, dotnet run, a local self-contained build) lists Dosai's
    // own dependencies as trusted platform assemblies too; referencing them bound an unrestored
    // tree's Roslyn or System.CommandLine calls to Dosai's copies, which no released build does.
    // Every build references the same framework assemblies as the bundled runtime.
    [Fact]
    public void FrameworkReferences_TrustedPlatformAssemblies_AreTheBundledFrameworkSet()
    {
        var current = FrameworkReferences.Current;

        Assert.Equal(FrameworkReferences.TrustedPlatformSource, current.Source);
        Assert.False(FrameworkReferences.IsFrameworkAssemblyName("Microsoft.CodeAnalysis"));
        Assert.False(FrameworkReferences.IsFrameworkAssemblyName("System.CommandLine"));
        var trusted = current.References.Select(reference => Path.GetFileNameWithoutExtension(reference.Key)).Order(StringComparer.Ordinal).ToList();
        var bundled = FrameworkReferences.FromLoadedAssemblies(FrameworkReferences.EmbeddedFrameworkAssemblyNames(), [])
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Key)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(bundled, trusted);
    }

    // The installed-framework fallback takes the newest Microsoft.NETCore.App of any version -
    // an older one still binds most of a project, where the previous floor at Dosai's own
    // version left none - and never ASP.NET Core's framework, which has no core library.
    [Fact]
    public void FrameworkReferences_SelectNewestInstalledFramework_AnyVersionCoreFrameworkOnly()
    {
        using var shared = new TemporaryDirectory();
        foreach (var version in new[] { "8.0.11", "10.0.0-rc.2.1", "9.0.3" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(shared.Path, "Microsoft.NETCore.App", version)).FullName;
            File.WriteAllText(Path.Combine(directory, "System.Runtime.dll"), string.Empty);
        }

        var aspNet = Directory.CreateDirectory(Path.Combine(shared.Path, "Microsoft.AspNetCore.App", "12.0.0")).FullName;
        File.WriteAllText(Path.Combine(aspNet, "System.Runtime.dll"), string.Empty);
        Directory.CreateDirectory(Path.Combine(shared.Path, "Microsoft.NETCore.App", "11.0.0"));

        var selected = FrameworkReferences.SelectNewestInstalledFramework([shared.Path]);

        Assert.NotNull(selected);
        Assert.Equal("10.0.0-rc.2.1", Path.GetFileName(selected.Value.Directory));
        Assert.Null(FrameworkReferences.SelectNewestInstalledFramework([Path.Combine(shared.Path, "missing")]));
    }

    // With no framework references at all, the slice says so and stops pointing at the tree:
    // the old output told the user to restore a tree that was already restored.
    [Fact]
    public void GetMethods_NoFrameworkReferences_SaysSoInsteadOfBlamingTheTree()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), ReproGreeter);

        MethodsSlice slice;
        using (FrameworkReferences.OverrideForTesting(new FrameworkReferenceSet([], FrameworkReferences.NoneSource, "No .NET framework metadata references could be resolved (test).", [])))
        {
            slice = ReadMethods(tempDirectory.Path);
        }

        Assert.Contains(slice.Diagnostics!, diagnostic => diagnostic.StartsWith("No .NET framework metadata references could be resolved", StringComparison.Ordinal));
        Assert.Contains(slice.Diagnostics!, diagnostic => diagnostic.Contains("Dosai resolved no framework metadata references", StringComparison.Ordinal));
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.Contains("Restore or build the tree", StringComparison.Ordinal));
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.TargetId == "Unresolved:System.Console.WriteLine");
    }

    // The reporter's fixture, with the framework references this process resolves: every call
    // binds, including the constructor and both framework calls.
    [Fact]
    public void GetMethods_ReproFixture_BindsFrameworkCalls()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), ReproGreeter);

        var edges = ReadMethods(tempDirectory.Path).CallGraph!.Edges.Select(edge => edge.TargetId).ToList();

        Assert.Contains("System.Console.WriteLine(string):void", edges);
        Assert.Contains("string.Concat(string,string):string", edges);
        Assert.Contains("Repro.Greeter..ctor()", edges);
        Assert.DoesNotContain(edges, target => target.StartsWith("Unresolved:", StringComparison.Ordinal));
    }

    // A restored project with no package dependencies resolves no package assemblies by
    // definition; only declared-but-missing packages deserve the restore hint.
    [Fact]
    public void GetMethods_AssetsWithoutPackages_DoesNotSuggestARestore()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), ReproGreeter);
        var obj = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "obj")).FullName;
        var packages = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "packages-cache")).FullName;
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), $$"""
{
  "version": 3,
  "targets": { "net8.0": {} },
  "libraries": {},
  "packageFolders": { "{{packages.Replace("\\", "\\\\")}}/": {} },
  "project": { "version": "1.0.0" }
}
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.Contains("no package assemblies were resolved", StringComparison.Ordinal));
    }

    // ----- Reference-assembly sources (issue #69) -----

    [Fact]
    public void ReferenceSources_Classify_RecognizesApiSurfaceStubsOnly()
    {
        var stub = CSharpSyntaxTree.ParseText("""
namespace Lib
{
    public partial interface ICacheEntry : System.IDisposable
    {
        long? Size { get; set; }
    }
    public partial class Cache
    {
        public Cache() { }
        public int Count { get { throw null; } set { } }
        public object Get(string key) { throw null; }
        public static Cache Create() => throw null;
        public class Nested { public void Run() { } }
    }
}
""");
        var implementation = CSharpSyntaxTree.ParseText("""
namespace Lib
{
    public class Cache
    {
        public object Get(string key) { throw null; }
        public int Count() => System.Environment.ProcessorCount;
    }
}
""");
        var interfaceOnly = CSharpSyntaxTree.ParseText("namespace Lib { public interface ICacheEntry { long? Size { get; set; } } }");
        var initializer = CSharpSyntaxTree.ParseText("""
namespace Lib
{
    public class Settings
    {
        public static readonly string Home = System.Environment.GetEnvironmentVariable("HOME");
        public object Get() { throw null; }
    }
}
""");

        const string defaultInterfaceMember = """
namespace Lib
{
    public partial interface IMemoryCache : System.IDisposable
    {
        object? GetCurrentStatistics() => null;
    }
}
""";
        // GenAPI's header marks a stub whose only member returns null; without it the same
        // file is ordinary code.
        var reviewed = CSharpSyntaxTree.ParseText("""
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

""" + defaultInterfaceMember);
        Assert.True(ReferenceSources.Classify(reviewed).IsReferenceSource);
        Assert.False(ReferenceSources.Classify(CSharpSyntaxTree.ParseText(defaultInterfaceMember)).IsReferenceSource);

        var classified = ReferenceSources.Classify(stub);
        Assert.True(classified.IsReferenceSource);
        Assert.Equal(["Lib.ICacheEntry`0", "Lib.Cache`0", "Lib.Cache`0+Nested`0"], classified.DeclaredTypes);
        Assert.False(ReferenceSources.Classify(implementation).IsReferenceSource);
        Assert.False(ReferenceSources.Classify(interfaceOnly).IsReferenceSource);
        Assert.False(ReferenceSources.Classify(initializer).IsReferenceSource);
    }

    // The issue's shape: a library's ref/ stubs and its src/ implementation in one tree. A stub
    // that only redeclares implemented types stays out of the compilation; one that also declares
    // a type nothing else does is compiled with just that type, on its original lines. The
    // property set on the implementation binds (it was CS0229-ambiguous with the stub's copy),
    // the stub-only type still binds, and both are reported. A stub folder on its own is kept.
    [Fact]
    public void GetMethods_ReferenceSourceBesideImplementation_IsLeftOutOfTheCompilation()
    {
        using var tempDirectory = new TemporaryDirectory();
        var library = Path.Combine(tempDirectory.Path, "Lib.Caching");
        Directory.CreateDirectory(Path.Combine(library, "ref"));
        Directory.CreateDirectory(Path.Combine(library, "src"));
        Directory.CreateDirectory(Path.Combine(library, "tests"));
        File.WriteAllText(Path.Combine(library, "ref", "Lib.Caching.cs"), """
namespace Lib.Caching
{
    public partial interface ICacheEntry : System.IDisposable
    {
        long? Size { get; set; }
        object Key { get; }
    }
    public static partial class CacheExtensions
    {
#if NET
        public static ICacheEntry SetSize(this ICacheEntry entry, long size) { throw null; }
#else
        public static ICacheEntry SetSize(this ICacheEntry entry, int size) { throw null; }
#endif
    }
}
namespace Lib.Caching.Unimplemented
{
    public partial class OnlyInRef
    {
        public void Run() { throw null; }
    }
}
""");
        File.WriteAllText(Path.Combine(library, "ref", "Lib.Caching.Manual.cs"), """
namespace Lib.Caching
{
    public static partial class CacheExtensions
    {
        public static ICacheEntry SetSize(this ICacheEntry entry, long size) { throw null; }
    }
}
""");
        File.WriteAllText(Path.Combine(library, "src", "ICacheEntry.cs"), """
namespace Lib.Caching
{
    public interface ICacheEntry : System.IDisposable
    {
        long? Size { get; set; }
        object Key { get; }
    }
}
""");
        File.WriteAllText(Path.Combine(library, "src", "CacheExtensions.cs"), """
namespace Lib.Caching
{
    public static class CacheExtensions
    {
        public static ICacheEntry SetSize(this ICacheEntry entry, long size)
        {
            entry.Size = size;
            return entry;
        }
    }
}
""");
        File.WriteAllText(Path.Combine(library, "tests", "CapacityTests.cs"), """
namespace Lib.Caching.Tests
{
    public class CapacityTests
    {
        public void SetsSize(Lib.Caching.ICacheEntry entry)
        {
            entry.Size = 4;
            new Lib.Caching.Unimplemented.OnlyInRef().Run();
        }
    }
}
""");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.Contains(slice.Diagnostics!, diagnostic => diagnostic.StartsWith("Skipped 1 reference-assembly source file(s)", StringComparison.Ordinal)
                                                         && diagnostic.Contains("Lib.Caching/ref/Lib.Caching.Manual.cs", StringComparison.Ordinal));
        Assert.Contains(slice.Diagnostics!, diagnostic => diagnostic.StartsWith("Trimmed 1 reference-assembly source file(s) to the 1 type(s)", StringComparison.Ordinal)
                                                         && diagnostic.Contains("Lib.Caching.Unimplemented.OnlyInRef (in Lib.Caching/ref/Lib.Caching.cs)", StringComparison.Ordinal));
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.SourceId.StartsWith("Lib.Caching.Tests.CapacityTests.SetsSize", StringComparison.Ordinal)
                                                       && edge.TargetId == "Lib.Caching.ICacheEntry.set_Size(long?):void");
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.SourceId.StartsWith("Lib.Caching.Tests.CapacityTests.SetsSize", StringComparison.Ordinal)
                                                       && edge.TargetId == "Lib.Caching.Unimplemented.OnlyInRef.Run():void");
        var refMethod = Assert.Single(slice.Methods!, method => method.Path!.Replace('\\', '/').Contains("/ref/", StringComparison.Ordinal));
        Assert.Equal(("OnlyInRef", "Run", 21), (refMethod.ClassName, refMethod.Name, refMethod.LineNumber));
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.Contains("declared by more than one file", StringComparison.Ordinal));

        // The API surface alone is not a duplicate of anything and is analyzed as scanned.
        var refOnly = ReadMethods(Path.Combine(library, "ref"));
        Assert.DoesNotContain(refOnly.Diagnostics!, diagnostic => diagnostic.Contains("reference-assembly source", StringComparison.Ordinal));
        Assert.Contains(refOnly.Methods!, method => method is { ClassName: "OnlyInRef", Name: "Run" });
        Assert.Contains(refOnly.Methods!, method => method is { ClassName: "CacheExtensions", Name: "SetSize" });
    }

    // Trimming blanks only the redeclarations' tokens: line breaks, comments and preprocessor
    // directives survive, so the trimmed stub parses cleanly, keeps its line count and declares
    // only what nothing else does.
    [Fact]
    public void ReferenceSources_Partition_TrimsAStubToTheTypesOnlyItDeclares()
    {
        const string stub = """
// Changes to this file must follow the https://aka.ms/api-review process.
namespace Lib
{
    /// <summary>Implemented in src.</summary>
    [System.Obsolete("x")]
    public partial class Implemented
    {
#if NET
        public string Name => throw null;
#endif
        public const string Multiline = @"first
second";
        public partial class Nested { }
    }
    public delegate void Handler(object sender);
    public partial class StubOnly<T>
    {
        public T Get() { throw null; }
    }
}
""";
        const string implementation = """
namespace Lib
{
    public class Implemented
    {
        public string Name => nameof(Name);
        public class Nested { }
    }
    public delegate void Handler(object sender);
}
""";
        var parseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols("NET");
        var stubTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(stub, parseOptions, path: "ref/Lib.cs");
        var implementationTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(implementation, parseOptions, path: "src/Lib.cs");

        var partition = ReferenceSources.Partition([stubTree, implementationTree]);

        Assert.Empty(partition.Skipped);
        var trimmed = Assert.Single(partition.Trimmed);
        Assert.Equal(new[] { "Lib.StubOnly`1" }, trimmed.KeptTypes);
        Assert.Same(trimmed.Tree, partition.Kept[0]);
        Assert.Equal("ref/Lib.cs", trimmed.Tree.FilePath);
        Assert.Contains("NET", trimmed.Tree.Options.PreprocessorSymbolNames);
        Assert.Equal(stubTree.GetText().Lines.Count, trimmed.Tree.GetText().Lines.Count);
        Assert.DoesNotContain(trimmed.Tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(new[] { "Lib.StubOnly`1" }, partition.KeptClassifications[0].DeclaredTypes);
        var getLine = stubTree.GetText().Lines.IndexOf(stub.IndexOf("public T Get()", StringComparison.Ordinal));
        var trimmedGet = trimmed.Tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single();
        Assert.Equal(getLine, trimmedGet.GetLocation().GetLineSpan().StartLinePosition.Line);
        Assert.Contains("#if NET", trimmed.Tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Implemented", trimmed.Tree.GetRoot().DescendantTokens().Select(token => token.ValueText));
    }

    // Types declared by more than one file without all declarations being partial are the
    // other source of the duplicate-member ambiguity: per-platform variants compiled together.
    // Reported from syntax, so it costs nothing; legitimate partial types are not reported.
    [Fact]
    public void GetMethods_DuplicateTypeDeclarations_AreReported()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Handler.cs"), """
namespace App
{
    public class Handler
    {
        public void Run() { System.Console.WriteLine("unix"); }
    }
    public partial class Split { public void A() { } }
}
""");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Handler.Windows.cs"), """
namespace App
{
    public class Handler
    {
        public void Run() { System.Console.WriteLine("windows"); }
    }
    public partial class Split { public void B() { } }
}
""");

        var diagnostics = ReadMethods(tempDirectory.Path).Diagnostics!;

        var duplicate = Assert.Single(diagnostics, diagnostic => diagnostic.Contains("declared by more than one file", StringComparison.Ordinal));
        Assert.StartsWith("1 type(s)", duplicate, StringComparison.Ordinal);
        // Files in ordinal path order, whatever order the file system enumerated them in.
        Assert.Contains("App.Handler (Handler.Windows.cs, Handler.cs)", duplicate, StringComparison.Ordinal);
        Assert.DoesNotContain("Split", duplicate, StringComparison.Ordinal);
    }

    // ----- Nearest-project target frameworks -----

    // A net48 project beside a net8.0 one: each file's guards resolve against the project that
    // compiles it, nested project directories included, and a subdirectory of a project follows
    // its project. The per-project decisions are in the metadata.
    [Fact]
    public void GetMethods_MixedTargetProjects_ResolveGuardsPerNearestProject()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteProject(Path.Combine(tempDirectory.Path, "Legacy", "Legacy.csproj"), "<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>");
        WriteGuards(Path.Combine(tempDirectory.Path, "Legacy", "LegacyCode.cs"), "LegacyCode");
        WriteGuards(Path.Combine(tempDirectory.Path, "Legacy", "Internal", "LegacyInternal.cs"), "LegacyInternal");
        WriteProject(Path.Combine(tempDirectory.Path, "Modern", "Modern.csproj"), "<TargetFramework>net8.0</TargetFramework>");
        WriteGuards(Path.Combine(tempDirectory.Path, "Modern", "Sub", "ModernSub.cs"), "ModernSub");
        WriteProject(Path.Combine(tempDirectory.Path, "Modern", "Plugins", "Plugin.csproj"), "<TargetFramework>netstandard2.0</TargetFramework>");
        WriteGuards(Path.Combine(tempDirectory.Path, "Modern", "Plugins", "Plugin.cs"), "Plugin");
        WriteGuards(Path.Combine(tempDirectory.Path, "Shared", "Linked.cs"), "Linked");

        var slice = ReadMethods(tempDirectory.Path);

        string ArmOf(string className) => Assert.Single(slice.Methods!, method => method.ClassName == className).Name!;
        Assert.Equal("FrameworkArm", ArmOf("LegacyCode"));
        Assert.Equal("FrameworkArm", ArmOf("LegacyInternal"));
        Assert.Equal("Net8Arm", ArmOf("ModernSub"));
        Assert.Equal("StandardArm", ArmOf("Plugin"));
        // Outside every project: the root-wide representative, the most modern detected target.
        Assert.Equal("Net8Arm", ArmOf("Linked"));

        var metadata = slice.Metadata!;
        Assert.Equal("net8.0", metadata.GuardTargetFramework);
        Assert.Equal(
            ["Legacy/Legacy.csproj=net48", "Modern/Modern.csproj=net8.0", "Modern/Plugins/Plugin.csproj=netstandard2.0"],
            metadata.ProjectGuardTargetFrameworks!.Select(project => $"{project.Project}={project.GuardTargetFramework}"));
        Assert.Contains(slice.Diagnostics!, diagnostic => diagnostic.Contains("evaluated per project", StringComparison.Ordinal));
    }

    // A project whose own file names no readable target inherits one from the nearest
    // Directory.Build.props; one whose target is an MSBuild property reference falls back to
    // the scan root's detection, as before.
    [Fact]
    public void GetMethods_ProjectTargets_InheritFromDirectoryBuildPropsElseFallBackToTheRoot()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteProject(Path.Combine(tempDirectory.Path, "legacy", "Directory.Build.props"), "<TargetFramework>net472</TargetFramework>");
        WriteProject(Path.Combine(tempDirectory.Path, "legacy", "App", "App.csproj"), "<OutputType>Library</OutputType>");
        WriteGuards(Path.Combine(tempDirectory.Path, "legacy", "App", "App.cs"), "Inherited");
        WriteProject(Path.Combine(tempDirectory.Path, "eng", "Engine.csproj"), "<TargetFrameworks>$(NetCoreAppCurrent);$(NetFrameworkMinimum)</TargetFrameworks>");
        WriteGuards(Path.Combine(tempDirectory.Path, "eng", "Engine.cs"), "Unreadable");
        WriteProject(Path.Combine(tempDirectory.Path, "Root.csproj"), "<TargetFramework>net8.0</TargetFramework>");

        var slice = ReadMethods(tempDirectory.Path);

        Assert.Equal("FrameworkArm", Assert.Single(slice.Methods!, method => method.ClassName == "Inherited").Name);
        Assert.Equal("Net8Arm", Assert.Single(slice.Methods!, method => method.ClassName == "Unreadable").Name);
    }

    // The F# frontend's `#if` tracking follows the nearest fsproj the same way.
    [Fact]
    public void GetMethods_FSharpGuards_ResolveAgainstTheNearestFsproj()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteProject(Path.Combine(tempDirectory.Path, "Legacy", "Legacy.fsproj"), "<TargetFramework>net48</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Legacy", "Legacy.fs"), """
module Legacy

#if NETFRAMEWORK
let frameworkArm () = 1
#else
let net8Arm () = 2
#endif
""");
        WriteProject(Path.Combine(tempDirectory.Path, "Modern", "Modern.fsproj"), "<TargetFramework>net8.0</TargetFramework>");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Modern", "Modern.fs"), """
module Modern

#if NETFRAMEWORK
let frameworkArm () = 1
#else
let net8Arm () = 2
#endif
""");

        var methods = ReadMethods(tempDirectory.Path).Methods!;

        Assert.Contains(methods, method => method is { FileName: "Legacy.fs", Name: "frameworkArm" });
        Assert.DoesNotContain(methods, method => method is { FileName: "Legacy.fs", Name: "net8Arm" });
        Assert.Contains(methods, method => method is { FileName: "Modern.fs", Name: "net8Arm" });
        Assert.DoesNotContain(methods, method => method is { FileName: "Modern.fs", Name: "frameworkArm" });
    }

    // ----- Field-like events -----

    // A field-like event's declarator declares the event: resolved as the event symbol it is,
    // the record carries its real namespace, containing type and interfaces (the old field-symbol
    // cast was always null and fell back to the compilation's name).
    [Fact]
    public void GetMethods_FieldLikeEvent_ResolvesItsEventSymbol()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Events.cs"), """
using System;
namespace Ev
{
    public interface INotify { }
    public class Publisher : INotify
    {
        public event EventHandler Changed, Closed;
        public event EventHandler Named { add { } remove { } }
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
""");

        var events = ReadMethods(tempDirectory.Path).Events!;

        Assert.Equal(3, events.Count);
        Assert.All(events, record =>
        {
            Assert.Equal("Ev", record.Namespace);
            Assert.Equal("Publisher", record.ClassName);
            Assert.Equal(["INotify"], record.ImplementedInterfaces!);
            Assert.Equal("System.EventHandler", record.TypeFullName);
        });
        Assert.Equal(["Changed", "Closed", "Named"], events.Select(record => record.Name!).Order(StringComparer.Ordinal));
    }

    private static void WriteProject(string path, string properties)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
    }

    private static void WriteGuards(string path, string className)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
public static class {{className}}
{
#if NETFRAMEWORK
    public static void FrameworkArm() { }
#elif NETSTANDARD
    public static void StandardArm() { }
#elif NET8_0
    public static void Net8Arm() { }
#else
    public static void OtherArm() { }
#endif
}
""");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-compilation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
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
