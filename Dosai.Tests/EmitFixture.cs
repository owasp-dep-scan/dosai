using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

/// <summary>Compiles a C# source into a fixture assembly with a portable PDB, for tests that need a built tree.</summary>
internal static class EmitFixture
{
    public static string Emit(string outputDirectory, string assemblyName, string source, string document, params string[] referencePaths)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference)
            .Concat(referencePaths.Select(path => MetadataReference.CreateFromFile(path)))
            .ToList();
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: document)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb")))
        {
            var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return dllPath;
    }
}
