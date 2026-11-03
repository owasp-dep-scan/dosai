using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Dosai.Tests;

// Issue #113: a tree holding a built Blazor WebAssembly client (or any other build output that
// carries the runtime's implementation assemblies, such as a self-contained publish) offered
// System.Private.CoreLib and the implementation facades beside a base reference pack - which
// holds no System.Private.CoreLib, its System.Runtime declaring the primitive types. Two
// corlibs made Roslyn report CS0518 for every predefined type and collapsed binding tree-wide:
// CreateBuilder, MapGet, Run and Uri.Host dropped out of Api/Program.cs. The fix keeps runtime
// implementation assemblies found in build output or the restore cache out of the compilation;
// the corlib always comes from the framework references. The tests build their fixtures from
// the files of the running runtime and the installed reference pack, so no other SDK, no wasm
// workload and no network is needed.
public partial class DosaiTests
{
    private const string RuntimeImplementationDiagnosticMarker = "runtime implementation assembly file(s) from the tree's build or restore output were left out";

    private const string Issue113ApiProgramSource = """
        public static class ApiProgram
        {
            public static string Run()
            {
                var line = System.Console.ReadLine();
                var uri = new System.Uri("https://example.org/" + line);
                System.Console.WriteLine(uri.Host);
                return uri.Host;
            }

            public static byte[] Digest(byte[] data) => System.Security.Cryptography.SHA256.HashData(data);
        }
        """;

    private const string Issue113FacadeSource = """
        [assembly: System.Reflection.AssemblyVersion("{0}")]

        namespace Issue113;

        public static class Facade
        {{
            public static string Value() => "issue113";
        }}
        """;

    /// <summary>
    ///     The reference-assembly directory of the installed Microsoft.NETCore.App.Ref pack for
    ///     the running runtime's major, located from the dotnet root next to the running runtime.
    ///     An exact-major pack keeps the fixture versions deterministic (a nearest-major pack
    ///     would change which offers win the version comparison).
    /// </summary>
    private static string FindCoreReferencePackOrSkip()
    {
        var coreLibrary = typeof(object).Assembly.Location;
        Skip.If(string.IsNullOrEmpty(coreLibrary), "The test host is single-file; the reference pack is located from the running runtime's directory.");
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(coreLibrary)!, "..", "..", ".."));
        var notes = new List<string>();
        var pack = FrameworkReferences.ResolvePackDirectory("Microsoft.NETCore.App", $"net{Environment.Version.Major}.0", Environment.Version.Major, notes, [Path.Combine(dotnetRoot, "packs")]);
        Skip.If(pack is null || !pack.Value.ExactMajor,
            $"No Microsoft.NETCore.App.Ref pack for .NET {Environment.Version.Major} is installed under {dotnetRoot} ({(pack is null ? string.Join(" ", notes) : $"resolved {pack.Value.Version} instead")}).");
        return pack!.Value.Directory;
    }

    private static List<(string Key, PortableExecutableReference Reference)> PackReferences(string packDirectory) =>
        Directory.EnumerateFiles(packDirectory, "*.dll").Order(StringComparer.Ordinal)
            .Select(path => (path, MetadataReference.CreateFromFile(path)))
            .ToList();

    private static string RuntimeDirectoryOrSkip()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Skip.If(string.IsNullOrEmpty(runtimeDirectory), "The test host is single-file; the runtime's implementation files are needed beside it.");
        return runtimeDirectory!;
    }

    /// <summary>The running runtime's core library, its Uri implementation and the System.Runtime facade, copied where a client's build output would hold them, under the deps.json that names them (the browser runtime pack in a real client).</summary>
    private static string CopyRuntimeFilesToClientBin(string root)
    {
        var runtimeDirectory = RuntimeDirectoryOrSkip();
        var bin = Path.Combine(root, "Client", "bin", "Debug", $"net{Environment.Version.Major}.0");
        CopyRuntimeFile(runtimeDirectory, "System.Private.CoreLib.dll", bin);
        CopyRuntimeFile(runtimeDirectory, "System.Private.Uri.dll", bin);
        CopyRuntimeFile(runtimeDirectory, "System.Runtime.dll", bin);
        // A built client's deps.json lists the runtime pack's files as package assets, so the
        // assembly scoping classifies them by metadata instead of the name heuristic.
        File.WriteAllText(Path.Combine(bin, "Client.deps.json"), $$"""
            {
              "version": 3,
              "targets": {
                "net{{Environment.Version.Major}}.0": {
                  "Microsoft.NETCore.App.Runtime.browser-wasm/11.0.0": {
                    "type": "package",
                    "runtime": {
                      "System.Private.CoreLib.dll": {},
                      "System.Private.Uri.dll": {},
                      "System.Runtime.dll": {}
                    }
                  }
                }
              },
              "libraries": {
                "Microsoft.NETCore.App.Runtime.browser-wasm/11.0.0": {
                  "type": "package",
                  "serviceable": true
                }
              }
            }
            """);
        return bin;
    }

    private static void CopyRuntimeFile(string runtimeDirectory, string fileName, string bin)
    {
        Directory.CreateDirectory(bin);
        var source = Path.Combine(runtimeDirectory, fileName);
        Skip.If(!File.Exists(source), $"'{fileName}' is not beside the running runtime ({runtimeDirectory}).");
        File.Copy(source, Path.Combine(bin, fileName));
    }

    /// <summary>The issue's Api project, adapted to what the base reference pack binds: Run over plain Uri and Console calls, plus the SHA256 call the crypto assertions look for.</summary>
    private static void WriteApiProject(string root)
    {
        var api = Path.Combine(root, "Api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Api.csproj"), $$"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net{{Environment.Version.Major}}.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(api, "Program.cs"), Issue113ApiProgramSource);
    }

    /// <summary>
    ///     Compiles against exactly the given references - not <see cref="FrameworkReferences.Current" /> -
    ///     so a fixture compiled against the pack references System.Runtime (package-shaped) and
    ///     one compiled against the runtime's implementation directory references
    ///     System.Private.CoreLib, the shape the fix detects from metadata.
    /// </summary>
    private static string EmitWithReferences(string outputDirectory, string assemblyName, string source, IReadOnlyList<MetadataReference> references)
    {
        Directory.CreateDirectory(outputDirectory);
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: $"{assemblyName}.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        var emit = compilation.Emit(dllPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    /// <summary>The slice's JSON with the run's timestamp overwritten, so two runs compare byte for byte.</summary>
    private static string Scrubbed(MethodsSlice slice)
    {
        var node = JsonSerializer.SerializeToNode(slice, Depscan.Dosai.Options)!.AsObject();
        node["Metadata"]?["GeneratedAt"]?.ReplaceWith("scrubbed");
        RemoveRuntimeImplementationDiagnostic(node["Diagnostics"] as JsonArray);
        return node.ToJsonString();
    }

    private static void RemoveRuntimeImplementationDiagnostic(JsonArray? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        for (var index = diagnostics.Count - 1; index >= 0; index--)
        {
            if (diagnostics[index]!.GetValue<string>().Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal))
            {
                diagnostics.RemoveAt(index);
            }
        }
    }

    private static List<(string? Called, AnalysisEvidenceKind Evidence)> ApiCalls(MethodsSlice slice) =>
        (slice.MethodCalls ?? [])
            .Where(call => (call.Path ?? string.Empty).Replace('\\', '/').StartsWith("Api/", StringComparison.Ordinal))
            .OrderBy(call => call.CalledMethod, StringComparer.Ordinal)
            .ThenBy(call => call.LineNumber)
            .Select(call => (call.CalledMethod, call.EvidenceKind))
            .ToList();

    // The core regression: build output holding the runtime's own files beside a reference pack.
    // Nothing under the client's bin becomes a reference, the pack's System.Runtime stays, the
    // skip is reported by name and version, and the compilation binds Uri.Host - while the old
    // shape (the same pack plus the copied corlib added by hand) still produces CS0518, which is
    // what the tree looked like before the fix.
    [SkippableFact]
    public void CompilationReferenceSet_RuntimeImplementationFilesInClientBin_NeverBecomeCompileReferences()
    {
        var packDirectory = FindCoreReferencePackOrSkip();
        var framework = PackReferences(packDirectory);
        // The premise the fix leans on: a reference pack holds no core library.
        Assert.DoesNotContain(framework, entry => Path.GetFileNameWithoutExtension(entry.Key) == "System.Private.CoreLib");

        using var tempDirectory = new TemporaryDirectory();
        var bin = CopyRuntimeFilesToClientBin(tempDirectory.Path);
        var coreLibCopy = Path.Combine(bin, "System.Private.CoreLib.dll");
        var uriCopy = Path.Combine(bin, "System.Private.Uri.dll");
        var runtimeCopy = Path.Combine(bin, "System.Runtime.dll");
        var coreLibVersion = typeof(object).Assembly.GetName()!.Version!.ToString();

        var created = new List<string>();
        var set = new CompilationReferenceSet(framework);
        set.AddBuildOutput(coreLibCopy, Track(created));
        set.AddBuildOutput(uriCopy, Track(created));
        set.AddBuildOutput(runtimeCopy, Track(created));

        var references = set.References();
        Assert.DoesNotContain(references, reference => reference.FilePath is not null && reference.FilePath.StartsWith(bin, StringComparison.Ordinal));
        Assert.Contains(references, reference => string.Equals(reference.FilePath, Path.Combine(packDirectory, "System.Runtime.dll"), StringComparison.Ordinal));
        // A skip is decided before a reference is built, so nothing under the client's bin was
        // read into one.
        Assert.DoesNotContain(created, path => path.StartsWith(bin, StringComparison.Ordinal));

        // The equal-version System.Runtime copy loses the tie against the pack's framework
        // reference before any guard asks; the two System.Private.* copies are the guard's own
        // skips.
        var line = Assert.Single(set.Diagnostics(), diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));
        Assert.Equal($"2 {RuntimeImplementationDiagnosticMarker} of the source compilation (System.Private.CoreLib {coreLibVersion}, System.Private.Uri {coreLibVersion}): the app runs on them but compiles against the reference packs, and the corlib comes from the framework references.", line);

        const string snippet = """
            public static class Probe
            {
                public static string Host()
                {
                    var uri = new System.Uri("https://example.org/path");
                    System.Console.WriteLine(uri.Host);
                    return uri.Host;
                }
            }
            """;
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var fixedCompilation = CSharpCompilation.Create("issue113-probe", [CSharpSyntaxTree.ParseText(snippet)], references, options);
        Assert.Empty(fixedCompilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var oldCompilation = CSharpCompilation.Create("issue113-probe-old", [CSharpSyntaxTree.ParseText(snippet)],
            framework.Select(entry => entry.Reference).Append(MetadataReference.CreateFromFile(coreLibCopy)), options);
        Assert.Contains(oldCompilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0518" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static Func<string, PortableExecutableReference?> Track(List<string> created) => path =>
    {
        created.Add(path);
        return MetadataReference.CreateFromFile(path);
    };

    // End to end through the methods pipeline: a tree whose client's bin carries the runtime's
    // files binds Api/Program.cs exactly like the same tree without them, and the outputs
    // differ only in the new diagnostic (and the run's timestamp).
    [SkippableFact]
    public void GetMethods_ClientBinHoldingTheRuntimeFiles_BindsTheSameAsATreeWithoutThem()
    {
        var packReferences = PackReferences(FindCoreReferencePackOrSkip());
        using var tempDirectory = new TemporaryDirectory();
        WriteApiProject(tempDirectory.Path);
        var clientBin = CopyRuntimeFilesToClientBin(tempDirectory.Path);

        MethodsSlice withRuntimeFiles;
        using (FrameworkReferences.OverrideForTesting(new FrameworkReferenceSet(packReferences, "issue113-test", null, [])))
        {
            withRuntimeFiles = Depscan.Dosai.GetMethodsSlice(tempDirectory.Path);
        }

        // Only the runtime's files go: the client's deps.json stays, because the package tables
        // and the assembly scoping read it in a real tree, in both shapes.
        foreach (var file in Directory.EnumerateFiles(clientBin, "*.dll"))
        {
            File.Delete(file);
        }

        MethodsSlice withoutRuntimeFiles;
        using (FrameworkReferences.OverrideForTesting(new FrameworkReferenceSet(packReferences, "issue113-test", null, [])))
        {
            withoutRuntimeFiles = Depscan.Dosai.GetMethodsSlice(tempDirectory.Path);
        }

        var expected = ApiCalls(withoutRuntimeFiles);
        Assert.NotEmpty(expected);
        Assert.All(expected, call => Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, call.Evidence));
        Assert.Contains(expected, call => call.Called?.Contains("System.Uri.Host", StringComparison.Ordinal) == true);
        Assert.Contains(expected, call => call.Called?.StartsWith("System.Console.WriteLine", StringComparison.Ordinal) == true);
        Assert.Equal(expected, ApiCalls(withRuntimeFiles));

        Assert.Equal(Scrubbed(withoutRuntimeFiles), Scrubbed(withRuntimeFiles));
        Assert.Contains(withRuntimeFiles.Diagnostics!, diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(withoutRuntimeFiles.Diagnostics!, diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));
    }

    // The data-flow and crypto compilations build their references the same way: the runtime's
    // files in the client's bin are reported as skipped, and the fixture's framework calls
    // (Console.ReadLine as the flow's source, SHA256.HashData for the crypto operation) still
    // resolve to their bound symbols.
    [SkippableFact]
    public void GetDataFlowsAndCrypto_ClientBinHoldingTheRuntimeFiles_ReportTheSkipAndKeepBindings()
    {
        var packReferences = PackReferences(FindCoreReferencePackOrSkip());
        using var tempDirectory = new TemporaryDirectory();
        WriteApiProject(tempDirectory.Path);
        CopyRuntimeFilesToClientBin(tempDirectory.Path);

        using (FrameworkReferences.OverrideForTesting(new FrameworkReferenceSet(packReferences, "issue113-test", null, [])))
        {
            var dataFlows = DataFlowAnalyzer.Analyze(tempDirectory.Path);
            var crypto = CryptoAnalyzer.Analyze(tempDirectory.Path);

            Assert.Contains(dataFlows.Diagnostics, diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));
            Assert.Contains(crypto.Diagnostics, diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));

            Assert.Contains(dataFlows.Nodes, node => node.Symbol?.StartsWith("System.Console.ReadLine", StringComparison.Ordinal) == true);
            Assert.Contains(crypto.Operations, operation => (operation.Symbol ?? operation.Code)?.Contains("SHA256", StringComparison.Ordinal) == true);
        }
    }

    // The metadata half of the guard: an assembly that references System.Private.CoreLib - the
    // shape of every runtime implementation file, emitted here against the runtime's own
    // implementation directory - does not replace a framework reference that lives in the pack's
    // System.Runtime world, however much higher its version. Its package-shaped twin, compiled
    // against the pack so it references System.Runtime, still replaces it (issue #81's rule).
    [SkippableFact]
    public void CompilationReferenceSet_CoreLibReferencingCopy_DoesNotReplaceAPackShapedFrameworkCopy()
    {
        var packReferences = PackReferences(FindCoreReferencePackOrSkip());
        using var tempDirectory = new TemporaryDirectory();
        var packShaped = EmitWithReferences(Path.Combine(tempDirectory.Path, "pack"), "Issue113.Facade",
            string.Format(CultureInfo.InvariantCulture, Issue113FacadeSource, "8.0.0.0"), packReferences.Select(entry => entry.Reference).ToList());
        var runtimeDirectory = RuntimeDirectoryOrSkip();
        var implementationShaped = EmitWithReferences(Path.Combine(tempDirectory.Path, "runtime"), "Issue113.Facade",
            string.Format(CultureInfo.InvariantCulture, Issue113FacadeSource, "99.0.0.0"),
            [
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Private.CoreLib.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
            ]);

        var set = new CompilationReferenceSet(packReferences.Append((packShaped, MetadataReference.CreateFromFile(packShaped))));
        set.AddBuildOutput(implementationShaped, path => MetadataReference.CreateFromFile(path));

        var references = set.References();
        Assert.Contains(references, reference => reference.FilePath == packShaped);
        Assert.DoesNotContain(references, reference => reference.FilePath == implementationShaped);
        var diagnostics = set.Diagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal) && diagnostic.Contains("compilation (Issue113.Facade 99.0.0.0):", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Contains("framework reference(s) were replaced", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void CompilationReferenceSet_PackageShapedCopyOfAFrameworkName_StillReplacesTheFrameworkCopy()
    {
        var packReferences = PackReferences(FindCoreReferencePackOrSkip());
        using var tempDirectory = new TemporaryDirectory();
        var packShaped = EmitWithReferences(Path.Combine(tempDirectory.Path, "pack"), "Issue113.Facade",
            string.Format(CultureInfo.InvariantCulture, Issue113FacadeSource, "8.0.0.0"), packReferences.Select(entry => entry.Reference).ToList());
        var packageShaped = EmitWithReferences(Path.Combine(tempDirectory.Path, "package"), "Issue113.Facade",
            string.Format(CultureInfo.InvariantCulture, Issue113FacadeSource, "99.0.0.0"), packReferences.Select(entry => entry.Reference).ToList());

        var set = new CompilationReferenceSet(packReferences.Append((packShaped, MetadataReference.CreateFromFile(packShaped))));
        set.AddBuildOutput(packageShaped, path => MetadataReference.CreateFromFile(path));

        var references = set.References();
        Assert.Contains(references, reference => reference.FilePath == packageShaped);
        Assert.DoesNotContain(references, reference => reference.FilePath == packShaped);
        var diagnostics = set.Diagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Contains("framework reference(s) were replaced by a higher-version copy", StringComparison.Ordinal) && diagnostic.Contains("Issue113.Facade 99.0.0.0 over 8.0.0.0", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));
    }

    // The restore cache is guarded by the same prefix rule, before the file is read into a
    // reference.
    [SkippableFact]
    public void CompilationReferenceSet_SystemPrivateAssemblyInRestoreCache_IsSkippedBeforeItIsRead()
    {
        var framework = PackReferences(FindCoreReferencePackOrSkip());
        using var tempDirectory = new TemporaryDirectory();
        var cache = Path.Combine(tempDirectory.Path, "cache", "microsoft.netcore.app.runtime", $"net{Environment.Version.Major}.0");
        var coreLib = Path.Combine(cache, "System.Private.CoreLib.dll");
        CopyRuntimeFile(RuntimeDirectoryOrSkip(), "System.Private.CoreLib.dll", cache);
        var coreLibVersion = typeof(object).Assembly.GetName()!.Version!.ToString();

        var created = 0;
        var set = new CompilationReferenceSet(framework);
        set.AddRestoreCache(coreLib, path =>
        {
            created++;
            return NuGetRestoreCache.TryCreateUnpinnedReference(path);
        });

        Assert.Equal(framework.Count, set.References().Count);
        Assert.Equal(0, created);
        var line = Assert.Single(set.Diagnostics(), diagnostic => diagnostic.Contains(RuntimeImplementationDiagnosticMarker, StringComparison.Ordinal));
        Assert.Equal($"1 {RuntimeImplementationDiagnosticMarker} of the source compilation (System.Private.CoreLib {coreLibVersion}): the app runs on them but compiles against the reference packs, and the corlib comes from the framework references.", line);
    }
}
