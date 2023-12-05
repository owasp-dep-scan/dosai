using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Depscan;

/// <summary>
/// Dotnet Source and Assembly Inspector
/// </summary>
public static class Dosai
{
    private static readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Get all assembly/source namespaces for the given path to assembly/source or directory of assemblies/source
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <returns>JSON list of assembly/source namespaces</returns>
    public static string GetNamespaces(string path)
    {
        var namespaces = GetAssemblyNamespaces(path);
        namespaces.AddRange(GetCSharpSourceNamespaces(path));

        return JsonSerializer.Serialize(namespaces, options);
    }

    /// <summary>
    /// Get all assembly namespaces for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly namespaces</returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static List<Namespace> GetAssemblyNamespaces(string path)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension);
        var assemblyNamespaces = new List<Namespace>();

        foreach(var assemblyFilePath in assembliesToInspect)
        {
            var assembly = Assembly.LoadFrom(assemblyFilePath);
            var namespaces = assembly.GetTypes()
                .Where(type => !string.IsNullOrEmpty(type.Namespace))
                .Select(type => type.Namespace ?? throw new InvalidOperationException($"{nameof(type.Namespace)} was null"))
                .Distinct()
                .Select(ns => new Namespace
                    {
                        FileName = $"{assembly.GetName().Name}{Constants.AssemblyExtension}",
                        Name = ns
                    }
                )
                .ToArray();

            assemblyNamespaces.AddRange(namespaces);
        }

        return assemblyNamespaces;
    }

    /// <summary>
    /// Get all source namespaces for the given path to C# source or directory of C# source
    /// </summary>
    /// <param name="path">Filesystem path to C# source file or directory containing C# source files</param>
    /// <returns>List of source namespaces</returns>
    private static List<Namespace> GetCSharpSourceNamespaces(string path)
    {
        var sourceToInspect = GetFilesToInspect(path, Constants.CSharpSourceExtension);
        var sourceNamespaces = new List<Namespace>();

        foreach(var sourceFilePath in sourceToInspect)
        {
            var fileName = Path.GetFileName(sourceFilePath);
            var fileContent = File.ReadAllText(sourceFilePath);
            var syntaxTree = CSharpSyntaxTree.ParseText(fileContent);
            var root = syntaxTree.GetCompilationUnitRoot();
            var syntaxes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();

            foreach(var syntax in syntaxes)
            {
                string nameSpace = string.Empty;
                SyntaxNode? potentialNamespaceParent = syntax.Parent;

                // Keep moving "out" of nested classes etc until we get to a namespace or until we run out of parents
                while (potentialNamespaceParent != null &&
                        potentialNamespaceParent is not NamespaceDeclarationSyntax &&
                        potentialNamespaceParent is not FileScopedNamespaceDeclarationSyntax)
                {
                    potentialNamespaceParent = potentialNamespaceParent.Parent;
                }

                // Build up the final namespace by looping until we no longer have a namespace declaration
                if (potentialNamespaceParent is BaseNamespaceDeclarationSyntax namespaceParent)
                {
                    // We have a namespace. Use that as the type
                    nameSpace = namespaceParent.Name.ToString();

                    // Keep moving "out" of the namespace declarations until we run out of nested namespace declarations
                    while (true)
                    {
                        if (namespaceParent.Parent is not NamespaceDeclarationSyntax parent)
                        {
                            break;
                        }

                        // Add the outer namespace as a prefix to the final namespace
                        nameSpace = $"{namespaceParent.Name}.{nameSpace}";
                        namespaceParent = parent;
                    }
                }

                if (!sourceNamespaces.Exists(ns => ns.Name == nameSpace) && !string.IsNullOrEmpty(nameSpace))
                {
                    sourceNamespaces.Add(new Namespace
                    {
                        FileName = fileName,
                        Name = nameSpace
                    });
                }
            }
        }

        return sourceNamespaces;
    }

    /// <summary>
    /// Get all assembly/source methods for the given path to assembly/source or directory of assemblies/source
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <returns>JSON list of assembly/source methods</returns>
    public static string GetMethods(string path)
    {
        var methods = GetAssemblyMethods(path);
        methods.AddRange(GetCSharpSourceMethods(path));

        return JsonSerializer.Serialize(methods, options);
    }

    /// <summary>
    /// Get all assembly methods for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly methods</returns>
    private static List<Method> GetAssemblyMethods(string path)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension);
        var assemblyMethods = new List<Method>();
        var failedAssemblies = new List<String>();
        foreach(var assemblyFilePath in assembliesToInspect)
        {
            var fileName = Path.GetFileName(assemblyFilePath);
            try
            {
                var assembly = Assembly.LoadFrom(assemblyFilePath);
                var types = assembly.GetExportedTypes();

                foreach(var type in types)
                {
                    var methods = type.GetMethods();

                    foreach(var method in methods)
                    {
                        if ($"{method.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName)
                        {
                            assemblyMethods.Add(new Method
                            {
                                FileName = fileName,
                                Namespace = method.DeclaringType?.Namespace,
                                Class = type.Name,
                                Attributes = method.Attributes.ToString(),
                                Name = method.Name,
                                ReturnType = method.ReturnType.Name.Replace("`1", string.Empty),
                                Parameters = method.GetParameters().Select(p => new Parameter {
                                    Name = p.Name,
                                    Type = p.ParameterType.Name.Replace("`1", string.Empty)
                                }).ToList()
                            });
                        }
                    }
                }
            }
            catch (Exception e) when (e is System.IO.FileLoadException || e is System.IO.FileNotFoundException || e is System.BadImageFormatException)
            {
                failedAssemblies.Add(assemblyFilePath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unable to process {assemblyFilePath} due to: {e.Message}");
            }
        }

        return assemblyMethods;
    }

    /// <summary>
    /// Get all source methods for the given path to C# source or directory of C# source
    /// </summary>
    /// <param name="path">Filesystem path to C# source file or directory containing C# source files</param>
    /// <returns>List of source methods</returns>
    private static List<Method> GetCSharpSourceMethods(string path)
    {
        var sourcesToInspect = GetFilesToInspect(path, Constants.CSharpSourceExtension);
        var sourceMethods = new List<Method>();

        foreach(var sourceFilePath in sourcesToInspect)
        {
            var fileName = Path.GetFileName(sourceFilePath);
            var fileContent = File.ReadAllText(sourceFilePath);
            var tree = CSharpSyntaxTree.ParseText(fileContent);
            var root = tree.GetCompilationUnitRoot();
            var compilation = CSharpCompilation.Create("Source").AddSyntaxTrees(tree);
            var model = compilation.GetSemanticModel(tree);
            var methodDeclarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach(var methodDeclaration in methodDeclarations)
            {
                var modifiers = methodDeclaration.Modifiers;
                var method = model.GetDeclaredSymbol(methodDeclaration);

                var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                var lineNumber = codeSpan.StartLinePosition.Line + 1;
                var columnNumber = codeSpan.Span.Start.Character;

                if (method != null && method.DeclaredAccessibility == Accessibility.Public)
                {
                    sourceMethods.Add(new Method
                    {
                        FileName = fileName,
                        Namespace = method.ContainingNamespace.ToDisplayString(),
                        Class = method.ContainingType.Name,
                        Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                        Name = method?.Name,
                        ReturnType = method?.ReturnType.Name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        Parameters = method?.Parameters.Select(p => new Parameter {
                            Name = p.Name,
                            Type = p.Type?.ToString()
                        }).ToList()
                    });
                }
            }
        }

        return sourceMethods;
    }

    /// <summary>
    /// Get list of files to inspect for the given path and file extension
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <returns>List of files</returns>
    private static List<string> GetFilesToInspect(string path, string fileExtension)
    {
        var filesToInspect = new List<string>();
        var fileAttributes = File.GetAttributes(path);
        if (fileAttributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var inputFile in new DirectoryInfo(path).EnumerateFiles($"*{fileExtension}", SearchOption.AllDirectories))
            {
                filesToInspect.Add(inputFile.FullName);
            }
        }
        else
        {
            var extension = Path.GetExtension(path);

            if (!extension.Equals(Constants.AssemblyExtension) && !extension.Equals(Constants.CSharpSourceExtension))
            {
                throw new Exception($"The provided file path must reference a {fileExtension} file.");
            }

            if (extension.Equals(fileExtension))
            {
                filesToInspect.Add(path);
            }
        }
        return filesToInspect;
    }
}