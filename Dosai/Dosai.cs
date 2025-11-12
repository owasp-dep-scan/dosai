using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.IO.Compression;
using System.Runtime.Loader;
using CompilationUnitSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax;
using FieldDeclarationSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax;
using InvocationExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax;

namespace Depscan;

internal static partial class FSharpRegex
{
    [GeneratedRegex(@"^\s*module\s+([\w\.]+)", RegexOptions.Compiled)]
    internal static partial Regex Module();

    [GeneratedRegex(@"^\s*type\s+(\w+)", RegexOptions.Compiled)]
    internal static partial Regex Type();

    [GeneratedRegex(@"^\s*let\s+(rec\s+)?(\w+)", RegexOptions.Compiled)]
    internal static partial Regex Function();

    [GeneratedRegex(@"^\s*member\s+(\w+|\.)\.(\w+)", RegexOptions.Compiled)]
    internal static partial Regex Member();

    [GeneratedRegex(@"^\s*new\s*\(", RegexOptions.Compiled)]
    internal static partial Regex Constructor();

    [GeneratedRegex(@"^\s*open\s+([\w\.]+)", RegexOptions.Multiline | RegexOptions.Compiled)]
    internal static partial Regex Open();

    [GeneratedRegex(@"(\w+)\s*\(", RegexOptions.Compiled)]
    internal static partial Regex MethodCall();
}

/// <summary>
/// An enhanced AssemblyLoadContext that resolves dependencies from a list of specified search paths.
/// </summary>
internal class InspectionAssemblyLoadContext(IEnumerable<string> searchPaths) : AssemblyLoadContext(isCollectible: true)
{
    private readonly List<string> _searchDirectories = searchPaths.Distinct().ToList();

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        return (from dir in _searchDirectories select Path.Combine(dir, assemblyName.Name + ".dll") into potentialPath where File.Exists(potentialPath) select LoadFromAssemblyPath(potentialPath)).FirstOrDefault();
    }
}

/// <summary>
/// Dotnet Source and Assembly Inspector
/// </summary>
public static class Dosai
{
    #region Signature Helpers

    /// <summary>
    /// Generates a unique signature for an IMethodSymbol (Method or Constructor).
    /// Format: Namespace.ClassName.MethodName[[GenericParams]](ParamType1,ParamType2,...):ReturnType
    /// For constructors, ReturnType is typically omitted or the class name.
    /// </summary>
    private static string GenerateMethodSignature(IMethodSymbol? methodSymbol)
    {
        if (methodSymbol is null)
            return string.Empty;

        var ns = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var className = methodSymbol.ContainingType?.Name ?? "";
        var methodName = methodSymbol.Name;
        var returnType = methodSymbol.MethodKind == MethodKind.Constructor ? "" : (methodSymbol.ReturnType.ToDisplayString());

        var parameters = methodSymbol.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToList();
        var paramString = string.Join(",", parameters);

        var generics = "";
        if (methodSymbol.IsGenericMethod)
        {
            var genericParams = methodSymbol.TypeParameters.Select(tp => tp.Name).ToList();
            if (genericParams.Any())
            {
                generics = $"``{string.Join(",", genericParams)}"; // Using `` for method generics
            }
        }
        // If the containing type is generic, its parameters are part of the class name context

        var signature = $"{ns}.{className}.{methodName}{generics}({paramString})";
        if (!string.IsNullOrEmpty(returnType))
        {
            signature += $":{returnType}";
        }
        return signature;
    }

    #endregion

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Get all assembly/source methods for the given path to assembly/source or directory of assemblies/source
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <returns>JSON list of assembly/source methods</returns>
    public static string GetMethods(string path)
    {
        var methods = GetAssemblyMethods(path);
        var (sourceMethods, usings, methodCalls, properties, fields, events, constructors, callGraph, sourceAssemblyMapping) = GetSourceMethods(path, methods);
        var assemblyInformation = GetAssemblyInformation(path);
        methods.AddRange(sourceMethods);

        return JsonSerializer.Serialize(new MethodsSlice 
        { 
            Dependencies = usings, 
            Methods = methods, 
            MethodCalls = methodCalls, 
            Properties = properties,
            Fields = fields,
            Events = events,
            Constructors = constructors,
            CallGraph = callGraph,
            AssemblyInformation = assemblyInformation,
            SourceAssemblyMapping = sourceAssemblyMapping
        }, Options);
    }

    /// <summary>
    /// Gets methods, dependencies, and call graph information from a .nupkg file.
    /// </summary>
    /// <param name="nupkgPath">Path to the .nupkg file</param>
    /// <returns>JSON string containing the results</returns>
    public static string GetMethodsFromNupkg(string nupkgPath)
    {
        string? tempExtractionDir = null;
        try
        {
            tempExtractionDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempExtractionDir);
            Console.WriteLine($"Extracting NuGet package: {nupkgPath} to {tempExtractionDir}");
            using var archive = ZipFile.OpenRead(nupkgPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("package/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var entryExtension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (!IsDotNetExtension(entryExtension)) continue;
                var destinationPath = Path.Combine(tempExtractionDir, entry.FullName);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(destinationDir))
                {
                    if (destinationDir is not null) Directory.CreateDirectory(destinationDir);
                }
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
            return GetMethods(tempExtractionDir);

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing NuGet package {nupkgPath}: {ex.Message}");
            throw; 
        }
        finally
        {
            if (tempExtractionDir is not null && Directory.Exists(tempExtractionDir))
            {
                try
                {
                    Directory.Delete(tempExtractionDir, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    Console.WriteLine($"Warning: Could not delete temporary directory {tempExtractionDir}: {cleanupEx.Message}");
                }
            }
        }
    }

    private static bool IsDotNetExtension(string extension)
    {
        var relevantExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Constants.AssemblyExtension, Constants.ExeExtension,
            Constants.CSharpSourceExtension, Constants.VBSourceExtension, Constants.FSharpSourceExtension
        };
        return relevantExtensions.Contains(extension);
    }
    
    /// <summary>
    /// Discovers the paths of all installed .NET shared runtimes (like Microsoft.NETCore.App
    /// and Microsoft.AspNetCore.App) by executing 'dotnet --list-runtimes'.
    /// </summary>
    /// <returns>A HashSet of directory paths containing the shared runtime assemblies.</returns>
    private static HashSet<string> GetDotnetSharedRuntimePaths()
    {
        var runtimePaths = new HashSet<string>();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // The output format is like: Microsoft.NETCore.App 7.0.14 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]
            var matches = Regex.Matches(output, @"\[(.*?)\]");
            foreach (Match match in matches)
            {
                if (match.Groups.Count <= 1) continue;
                var runtimeDir = Path.GetDirectoryName(match.Groups[1].Value);
                if (runtimeDir is not null && Directory.Exists(runtimeDir))
                {
                    runtimePaths.Add(runtimeDir);
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Could not execute 'dotnet --list-runtimes' to find shared frameworks. Please ensure the .NET SDK is installed and that 'dotnet' is in your system's PATH. Details: {ex.Message}");
            Console.ResetColor();
        }
        return runtimePaths;
    }
    
    /// <summary>
    /// Get all assembly information for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly information</returns>
    private static List<AssemblyInformation> GetAssemblyInformation(string path)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension, Constants.ExeExtension);
        List<AssemblyInformation> assemblyInformation = [];
        List<string> failedAssemblies = [];

        foreach(var assemblyFilePath in assembliesToInspect)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(assemblyFilePath);

                if (assemblyInformation.Exists(item => item.Name == fileName))
                {
                    continue;
                }

                var fileVersionInfo = FileVersionInfo.GetVersionInfo(assemblyFilePath);
#pragma warning disable CS0472 // The result of the expression is always the same since a value of this type is never equal to 'null'
                var buildPart = $".{fileVersionInfo.FileBuildPart}";
                var privatePart = $".{fileVersionInfo.FilePrivatePart}";
#pragma warning restore CS0472 // The result of the expression is always the same since a value of this type is never equal to 'null'

                var assemblyInfo = new AssemblyInformation
                {
                    Name = fileName,
                    Version = $"{fileVersionInfo.FileMajorPart}.{fileVersionInfo.FileMinorPart}{buildPart}{privatePart}"
                };

                assemblyInformation.Add(assemblyInfo);
            }
            catch (Exception e) when (e is FileLoadException || e is FileNotFoundException || e is BadImageFormatException)
            {
                failedAssemblies.Add(assemblyFilePath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unable to process {assemblyFilePath} due to: {e.Message}");
            }
        }

        return assemblyInformation;
    }

    /// <summary>
    /// Get all assembly methods for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly methods</returns>
    private static List<Method> GetAssemblyMethods(string path)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension, Constants.ExeExtension);
        var assemblyMethods = new List<Method>();
        var processedAssemblyIdentities = new HashSet<string>();
        var sharedRuntimePaths = GetDotnetSharedRuntimePaths();
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var sharedDir = Path.GetFullPath(Path.Combine(runtimeDir, "..", ".."));
        var dependencyDirs = new List<string> { Path.GetDirectoryName(path)! };
        if (Directory.Exists(sharedDir))
        {
            if (Directory.Exists(Path.Combine(sharedDir, "Microsoft.NETCore.App")))
            {
                dependencyDirs.AddRange(Directory.GetDirectories(Path.Combine(sharedDir, "Microsoft.NETCore.App")));
            }

            if (Directory.Exists(Path.Combine(sharedDir, "Microsoft.AspNetCore.App")))
            {
                dependencyDirs.AddRange(Directory.GetDirectories(Path.Combine(sharedDir, "Microsoft.AspNetCore.App")));
            }
        }
        var failedAssemblies = new List<string>();

        foreach (var assemblyFilePath in assembliesToInspect)
        {
            var fileName = Path.GetFileName(assemblyFilePath);
            var searchPaths = new List<string> { Path.GetDirectoryName(assemblyFilePath)! };
            searchPaths.AddRange(dependencyDirs);
            searchPaths.AddRange(sharedRuntimePaths);
            var loadContext = new InspectionAssemblyLoadContext(searchPaths);
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyFilePath);
                if (processedAssemblyIdentities.Contains(assemblyName.FullName))
                {
                    continue;
                }
                var assembly = loadContext.LoadFromAssemblyName(assemblyName);
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Console.WriteLine($"Warning: Could not load all types from {fileName}. Some types will be skipped.");
                    if (ex.LoaderExceptions is not null)
                    {
                        var uniqueLoaderErrors = ex.LoaderExceptions
                            .Where(e => e is not null)
                            .Select(e => e?.Message)
                            .Distinct();

                        foreach (var errorMessage in uniqueLoaderErrors)
                        {
                            Console.WriteLine($"  - {errorMessage}");
                            if (errorMessage is null ||
                                !errorMessage.Contains("The system cannot find the file specified")) continue;
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("    Suggestion: This error often means a .NET Shared Framework is missing. Ensure the machine running this analysis has the necessary .NET SDKs and Runtimes (e.g., ASP.NET Core Runtime) installed. Some projects might require Windows for building.");
                            Console.ResetColor();
                        }
                    }
                    types = ex.Types.Where(t => t is not null).ToArray()!;
                }

                foreach (var type in types)
                {
                    foreach (var method in type.GetMethods())
                    {
                        if ($"{method.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" != fileName) continue;

                        var parameters = method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
                        var paramString = string.Join(",", parameters);
                        var returnType = method.ReturnType.FullName ?? method.ReturnType.Name;
                        var className = method.DeclaringType?.Name ?? "UnknownType";
                        var ns = method.DeclaringType?.Namespace ?? "";
                        var assemblySignature = $"{ns}.{className}.{method.Name}({paramString}):{returnType}";
                        if (method.Name is ".ctor" or ".cctor")
                        {
                            assemblySignature = $"{ns}.{className}.{method.Name}({paramString})";
                        }
                        
                        var methodParams = method.GetParameters().Select(p => new Parameter
                        {
                            Name = p.Name,
                            Type = p.ParameterType.FullName ?? p.ParameterType.Name,
                            TypeFullName = p.ParameterType.FullName ?? p.ParameterType.Name,
                            IsGenericParameter = p.ParameterType.IsGenericParameter
                        }).ToList();
                        
                        var genericParameters = method.IsGenericMethodDefinition
                            ? method.GetGenericArguments().Select(t => t.Name).ToList()
                            : [];

                        assemblyMethods.Add(CreateMethodObjectFromMember(
                            method, assemblyFilePath, fileName, method.Attributes.ToString(), method.Name, returnType,
                            methodParams, method.MetadataToken, assemblySignature,
                            method.IsGenericMethod, method.IsGenericMethodDefinition, genericParameters
                        ));
                    }
                    processedAssemblyIdentities.Add(assembly.FullName!);
                    assemblyMethods.AddRange(from ctor in type.GetConstructors() where $"{ctor.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName let ctorParams = ctor.GetParameters().Select(p => new Parameter { Name = p.Name, Type = p.ParameterType.FullName }).ToList() let assemblySignature = $"{ctor.DeclaringType?.Name}" select CreateMethodObjectFromMember(ctor, assemblyFilePath, fileName, ctor.Attributes.ToString(), ".ctor", "Void", ctorParams, ctor.MetadataToken, assemblySignature));
                    assemblyMethods.AddRange(from prop in type.GetProperties() where $"{prop.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName select CreateMethodObjectFromMember(prop, assemblyFilePath, fileName, "Property", prop.Name, prop.PropertyType.Name, []));
                    assemblyMethods.AddRange(from field in type.GetFields() where $"{field.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName select CreateMethodObjectFromMember(field, assemblyFilePath, fileName, field.Attributes.ToString(), field.Name, field.FieldType.Name, []));
                    assemblyMethods.AddRange(from evt in type.GetEvents() where $"{evt.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName select CreateMethodObjectFromMember(evt, assemblyFilePath, fileName, evt.Attributes.ToString(), evt.Name, evt.EventHandlerType?.Name ?? string.Empty, []));
                }
            }
            catch (Exception e) when (e is FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                failedAssemblies.Add(assemblyFilePath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unable to process {assemblyFilePath} due to: {e.Message}");
            }
            finally
            {
                loadContext.Unload();
            }
        }

        return assemblyMethods;

        Method CreateMethodObjectFromMember(
            MemberInfo member, string filePath, string file, string attributes, string name, string returnType,
            List<Parameter> parameters, int metadataToken = 0, string? assemblySignature = null,
            bool isGeneric = false, bool isGenericDef = false, List<string>? genericParams = null)
        {
            var typ = member.DeclaringType;
            var baseType = typ?.BaseType?.Name;
            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList() ?? [];

            return new Method
            {
                Path = filePath,
                FileName = file,
                Module = typ?.Module.ToString(),
                Namespace = typ?.Namespace,
                ClassName = typ?.Name ?? string.Empty,
                Attributes = attributes,
                Name = name,
                ReturnType = returnType,
                Parameters = parameters,
                CustomAttributes = member.GetCustomAttributesData().Select(attr =>
                    new CustomAttributeInfo
                    {
                        Name = attr.AttributeType.Name,
                        FullName = attr.AttributeType.FullName,
                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo
                        {
                            Name = na.MemberName,
                            Value = na.TypedValue.Value?.ToString() ?? string.Empty
                        }).ToList()
                    }).ToList(),
                BaseType = baseType,
                ImplementedInterfaces = implementedInterfaces,
                MetadataToken = metadataToken,
                AssemblySignature = assemblySignature,
                IsGenericMethod = isGeneric,
                IsGenericMethodDefinition = isGenericDef,
                GenericParameters = genericParams ?? []
            };
        }
    }

    private static string GetContainingTypeNameVb(Microsoft.CodeAnalysis.VisualBasic.Syntax.FieldDeclarationSyntax member)
    {
        var parent = member.Parent;
        while (parent is not null)
        {
            if (parent is ClassBlockSyntax classBlock)
                return classBlock.ClassStatement.Identifier.Text;
            if (parent is StructureBlockSyntax structBlock)
                return structBlock.StructureStatement.Identifier.Text;
            parent = parent.Parent;
        }
        return "";
    }
    
    /// <summary>
    /// Get all F# methods for the given path to F# source or directory of F# source
    /// </summary>
    /// <param name="path">Filesystem path to F# source file or directory containing F# source files</param>
    /// <returns>Tuple with List of F# methods, dependencies, and method calls</returns>
    private static (List<Method>, List<Dependency>, List<MethodCalls>) GetFSharpMethods(string path)
    {
        var sourcesToInspect = GetFilesToInspect(path, Constants.FSharpSourceExtension);
        var fsharpMethods = new List<Method>();
        var fsharpDependencies = new List<Dependency>();
        var fsharpMethodCalls = new List<MethodCalls>();

        foreach (var sourceFilePath in sourcesToInspect)
        {
            try
            {
                var fileName = Path.GetFileName(sourceFilePath);
                var fileContent = File.ReadAllText(sourceFilePath);
                var lines = fileContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                
                string currentModule = "Global";
                string currentType = "";
                
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    var lineNumber = i + 1;
                    
                    // Extract module declarations
                    var moduleMatch = FSharpRegex.Module().Match(line);
                    if (moduleMatch.Success)
                    {
                        currentModule = moduleMatch.Groups[1].Value;
                    }
                    
                    // Extract type declarations
                    var typeMatch = FSharpRegex.Type().Match(line);
                    if (typeMatch.Success)
                    {
                        currentType = typeMatch.Groups[1].Value;
                    }
                    
                    // Extract function declarations: "let functionName" or "let rec functionName"
                    var functionMatch = FSharpRegex.Function().Match(line);
                    if (functionMatch.Success)
                    {
                        var functionName = functionMatch.Groups[2].Value;
                        // Skip common F# keywords that might match the pattern
                        if (functionName is "rec" or "in" or "and") 
                            continue;
                        // Determine the containing context
                        var containingContext = !string.IsNullOrEmpty(currentType) ? currentType : currentModule;
                        
                        fsharpMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Name = functionName,
                            ClassName = containingContext,
                            Namespace = "Unknown",
                            Assembly = "Unknown",
                            Module = "Unknown",
                            Attributes = "Public",
                            ReturnType = "Unknown",
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf(functionName, StringComparison.Ordinal) + 1,
                            Parameters = [],
                            CustomAttributes = []
                        });
                    }
                    // Extract member declarations: "member this.MemberName" or "member _.MemberName"
                    var memberMatch = FSharpRegex.Member().Match(line);
                    if (memberMatch.Success)
                    {
                        var memberName = memberMatch.Groups[2].Value;
                        string containingType = !string.IsNullOrEmpty(currentType) ? currentType : "Unknown";
                        
                        fsharpMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Name = memberName,
                            ClassName = containingType,
                            Namespace = "Unknown",
                            Assembly = "Unknown",
                            Module = "Unknown",
                            Attributes = "Public",
                            ReturnType = "Unknown",
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf(memberName, StringComparison.Ordinal) + 1,
                            Parameters = [],
                            CustomAttributes = []
                        });
                    }
                    // Extract constructor declarations: "new(args) ="
                    var constructorMatch = FSharpRegex.Constructor().Match(line);
                    if (constructorMatch.Success)
                    {
                        string containingType = !string.IsNullOrEmpty(currentType) ? currentType : "Unknown";
                        
                        fsharpMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Name = ".ctor",
                            ClassName = containingType,
                            Namespace = "Unknown",
                            Assembly = "Unknown",
                            Module = "Unknown",
                            Attributes = "Public",
                            ReturnType = "Void",
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf("new", StringComparison.Ordinal) + 1,
                            Parameters = [],
                            CustomAttributes = []
                        });
                    }
                }
                // Extract dependencies (open statements)
                var openMatches = FSharpRegex.Open().Matches(fileContent);
                foreach (Match match in openMatches)
                {
                    var namespaceName = match.Groups[1].Value;
                    var lineIndex = fileContent.Substring(0, match.Index).Split('\n').Length;
                    
                    fsharpDependencies.Add(new Dependency
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        Name = namespaceName,
                        Namespace = namespaceName,
                        Assembly = "Unknown",
                        Module = "Unknown",
                        LineNumber = lineIndex,
                        ColumnNumber = match.Index - fileContent.LastIndexOf('\n', match.Index) + 1,
                        NamespaceMembers = []
                    });
                }
                // Extract method calls (function calls with parentheses)
                var callMatches = FSharpRegex.MethodCall().Matches(fileContent);
                foreach (Match match in callMatches)
                {
                    var methodName = match.Groups[1].Value;
                    // Skip common keywords
                    if (IsFSharpKeyword(methodName))
                        continue;
                    
                    var lineIndex = fileContent.Substring(0, match.Index).Split('\n').Length;
                    
                    fsharpMethodCalls.Add(new MethodCalls
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        CalledMethod = methodName,
                        ClassName = "Unknown",
                        Namespace = "Unknown",
                        Assembly = "Unknown",
                        Module = "Unknown",
                        LineNumber = lineIndex,
                        ColumnNumber = match.Index - fileContent.LastIndexOf('\n', match.Index) + 1,
                        Arguments = []
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing F# file {sourceFilePath}: {ex.Message}");
            }
        }

        return (fsharpMethods, fsharpDependencies, fsharpMethodCalls);
    }

    private static bool IsFSharpKeyword(string word)
    {
        var keywords = new HashSet<string>
        {
            "if", "then", "else", "elif", "for", "while", "do", "match", "with", "try", 
            "catch", "finally", "let", "rec", "and", "fun", "function", "in", "open",
            "module", "type", "exception", "namespace", "assembly", "begin", "end",
            "abstract", "default", "delegate", "enum", "extern", "fixed", "interface",
            "internal", "lazy", "mutable", "new", "null", "override", "private", 
            "protected", "public", "return", "static", "to", "true", "upcast", "use",
            "virtual", "void", "when", "yield"
        };
        
        return keywords.Contains(word);
    }

    private static Method CreateMethodFromSymbol(
        IMethodSymbol methodSymbol,
        SemanticModel model,
        string sourceFilePath,
        string fileName,
        string basePath,
        int lineNumber = 0,
        int columnNumber = 0)
    {
        if (lineNumber == 0 || columnNumber == 0)
        {
            var location = methodSymbol.Locations.FirstOrDefault();
            var lineSpan = location?.GetLineSpan();
            lineNumber = lineSpan?.StartLinePosition.Line + 1 ?? lineNumber;
            columnNumber = lineSpan?.Span.Start.Character + 1 ?? columnNumber;
        }

        var containingType = methodSymbol.ContainingType;
        var containingNamespace = methodSymbol.ContainingNamespace;
        var assembly = methodSymbol.ContainingAssembly;
        var module = methodSymbol.ContainingModule;

        var modifiers = new List<string>();
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Public)) modifiers.Add("Public");
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Private)) modifiers.Add("Private");
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Protected)) modifiers.Add("Protected");
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Internal)) modifiers.Add("Internal");
        if (methodSymbol.IsStatic) modifiers.Add("Static");
        if (methodSymbol.IsVirtual) modifiers.Add("Virtual");
        if (methodSymbol.IsOverride) modifiers.Add("Override");

        var isGenericMethod = methodSymbol.IsGenericMethod;
        var genericParameters = methodSymbol.TypeParameters.Select(tp => tp.Name).ToList();

        var metadataToken = 0;
        if (assembly is not null && SymbolEqualityComparer.Default.Equals(assembly, model.Compilation.Assembly))
        {
            metadataToken = methodSymbol.MetadataToken;
        }

        string sourceSignature = GenerateMethodSignature(methodSymbol);
        string assemblySignature = methodSymbol.ToDisplayString();

        var baseType = containingType?.BaseType?.Name ?? "Object";
        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList() ?? new List<string>();

        return new Method
        {
            Path = Path.GetRelativePath(basePath, sourceFilePath),
            FileName = fileName,
            Assembly = assembly?.ToDisplayString() ?? "",
            Module = module?.ToDisplayString() ?? "",
            Namespace = containingNamespace?.ToDisplayString() ?? "",
            ClassName = containingType?.Name ?? "",
            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
            Name = methodSymbol.Name,
            ReturnType = methodSymbol.ReturnType.ToDisplayString(),
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            Parameters = methodSymbol.Parameters.Select(p => new Parameter
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsGenericParameter = p.Type is ITypeParameterSymbol
            }).ToList(),
            CustomAttributes = methodSymbol.GetAttributes().Select(attr =>
                new CustomAttributeInfo
                {
                    Name = attr.AttributeClass?.Name,
                    FullName = attr.AttributeClass?.ToDisplayString(),
                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo
                    {
                        Name = na.Key,
                        Value = na.Value.Value?.ToString() ?? string.Empty
                    }).ToList()
                }).ToList(),
            BaseType = baseType,
            ImplementedInterfaces = implementedInterfaces,
            MetadataToken = metadataToken,
            SourceSignature = sourceSignature,
            AssemblySignature = assemblySignature,
            IsGenericMethod = isGenericMethod,
            GenericParameters = genericParameters,
        };
    }
    
    /// <summary>
    /// Get all source methods for the given path to C# source or directory of C# source
    /// </summary>
    /// <param name="path">Filesystem path to C# source file or directory containing C# source files</param>
    /// <param name="assemblyMethods">List of assembly methods</param>
    /// <returns>Tuple with List of source methods and using directives</returns>
    private static (List<Method>, List<Dependency>, List<MethodCalls>, List<PropertyInfo>, List<FieldInfo>, List<EventInfo>, List<ConstructorInfo>, CallGraph, List<SourceAssemblyMapping>) GetSourceMethods(string path, List<Method> assemblyMethods)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension, Constants.ExeExtension);
        var sourcesToInspect = GetFilesToInspect(path, Constants.CSharpSourceExtension);
        sourcesToInspect.AddRange(GetFilesToInspect(path, Constants.VBSourceExtension));
        sourcesToInspect.AddRange(GetFilesToInspect(path, Constants.FSharpSourceExtension));
        var sourceMethods = new List<Method>();
        var allUsingDirectives = new List<Dependency>();
        var allMethodCalls = new List<MethodCalls>();
        var properties = new List<PropertyInfo>();
        var fields = new List<FieldInfo>();
        var events = new List<EventInfo>();
        var constructors = new List<ConstructorInfo>();
        var sourceAssemblyMappings = new List<SourceAssemblyMapping>();
#pragma warning disable IL3000
        var mscorlib = MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, typeof(object).Assembly.Location));
#pragma warning restore IL3000
        var metadataReferences = new List<PortableExecutableReference>
        {
            mscorlib
        };
        metadataReferences.AddRange(assembliesToInspect.Select(externalAssembly => MetadataReference.CreateFromFile(externalAssembly)));

        foreach (var sourceFilePath in sourcesToInspect)
        {
            var fileName = Path.GetFileName(sourceFilePath);
            var fileContent = File.ReadAllText(sourceFilePath);
            var extn = Path.GetExtension(sourceFilePath);
            SemanticModel? model;
            CompilationUnitSyntax? csRoot = null;
            Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax? vbRoot = null;

            if (extn.Equals(Constants.CSharpSourceExtension))
            {
                var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(fileContent);
                csRoot = tree.GetCompilationUnitRoot();
                var compilation = CSharpCompilation.Create(fileName, syntaxTrees: [tree], references: metadataReferences);
                model = compilation.GetSemanticModel(tree);
            }
            else if (extn.Equals(Constants.VBSourceExtension))
            {
                var tree = (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(fileContent);
                vbRoot = tree.GetCompilationUnitRoot();
                var compilation = VisualBasicCompilation.Create(fileName, syntaxTrees: [tree], references: metadataReferences);
                model = compilation.GetSemanticModel(tree);
            }
            else
            {
                continue;
            }

            var csMethodDeclarations = csRoot?.DescendantNodes().OfType<MethodDeclarationSyntax>();
            var vbMethodDeclarations = vbRoot?.DescendantNodes().OfType<MethodStatementSyntax>();

            var csUsingDirectives = csRoot?.DescendantNodes().OfType<UsingDirectiveSyntax>();
            var vbImportsDirectives = vbRoot?.DescendantNodes().OfType<SimpleImportsClauseSyntax>();

            var csMethodCalls = csRoot?.DescendantNodes().OfType<InvocationExpressionSyntax>();
            var vbMethodCalls = vbRoot?.DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax>();

            var csPropertyDeclarations = csRoot?.DescendantNodes().OfType<PropertyDeclarationSyntax>();
            var vbPropertyDeclarations = vbRoot?.DescendantNodes().OfType<PropertyStatementSyntax>();

            var csFieldDeclarations = csRoot?.DescendantNodes().OfType<FieldDeclarationSyntax>();
            var vbFieldDeclarations = vbRoot?.DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.FieldDeclarationSyntax>();

            var csEventDeclarations = csRoot?.DescendantNodes().OfType<EventDeclarationSyntax>();
            var csEventFieldDeclarations = csRoot?.DescendantNodes().OfType<EventFieldDeclarationSyntax>();
            var vbEventDeclarations = vbRoot?.DescendantNodes().OfType<EventStatementSyntax>();
            
            var csConstructorDeclarations = csRoot?.DescendantNodes().OfType<ConstructorDeclarationSyntax>();
            var vbConstructorDeclarations = vbRoot?.DescendantNodes().OfType<ConstructorBlockSyntax>();

            // C# method declarations
            if (csMethodDeclarations is not null)
            {
                foreach(var methodDeclaration in csMethodDeclarations)
                {
                    var modifiers = methodDeclaration.Modifiers;
                    var methodSymbol = model.GetDeclaredSymbol(methodDeclaration);
                    var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (methodSymbol is not null)
                    {
                        // Get the containing type for inheritance information
                        var containingType = methodSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = methodSymbol.MetadataToken;
                        }
                        var isGenericMethod = methodSymbol.IsGenericMethod;
                        List<string> genericParameters = methodSymbol.TypeParameters.Select(tp => tp.Name).ToList();
                        sourceMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = methodSymbol.ContainingAssembly.ToDisplayString(),
                            Module = methodSymbol.ContainingModule.ToDisplayString(),
                            Namespace = methodSymbol.ContainingNamespace.ToDisplayString(),
                            ClassName = methodSymbol.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = methodSymbol.Name,
                            ReturnType = methodSymbol.ReturnType.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            Parameters = methodSymbol.Parameters.Select(p => new Parameter
                            {
                                Name = p.Name,
                                Type = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Type.ToString()!),
                                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                IsGenericParameter = p.Type is ITypeParameterSymbol
                            }).ToList(),
                            CustomAttributes = methodSymbol.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken,
                            SourceSignature = GenerateMethodSignature(methodSymbol),
                            IsGenericMethod = isGenericMethod,
                            GenericParameters = genericParameters,
                        });
                    }
                }
            }

            // VB method declarations
            if (vbMethodDeclarations is not null)
            {
                foreach(var methodDeclaration in vbMethodDeclarations)
                {
                    var modifiers = methodDeclaration.Modifiers;
                    var method = model.GetDeclaredSymbol(methodDeclaration);
                    var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (method is not null)
                    {
                        // Get inheritance information
                        var containingType = method.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = method.MetadataToken;
                        }
                        sourceMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = method.ContainingAssembly.ToDisplayString(),
                            Module = method.ContainingModule.ToDisplayString(),
                            Namespace = method.ContainingNamespace.ToDisplayString(),
                            ClassName = method.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = method.Name,
                            ReturnType = method.ReturnType.Name,
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            Parameters = method.Parameters.Select(p => new Parameter {
                                Name = p.Name,
                                Type = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Type.ToString()!),
                                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            }).ToList(),
                            CustomAttributes = method.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken,
                            SourceSignature = GenerateMethodSignature(method)
                        });
                    }
                }
            }

            // C# property declarations
            if (csPropertyDeclarations is not null)
            {
                foreach(var propertyDeclaration in csPropertyDeclarations)
                {
                    var modifiers = propertyDeclaration.Modifiers;
                    var propertySymbol = model.GetDeclaredSymbol(propertyDeclaration);
                    var codeSpan = propertyDeclaration.SyntaxTree.GetLineSpan(propertyDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (propertySymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = propertySymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = propertySymbol.MetadataToken;
                        }
                        properties.Add(new PropertyInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = propertySymbol.ContainingAssembly.ToDisplayString(),
                            Module = propertySymbol.ContainingModule.ToDisplayString(),
                            Namespace = propertySymbol.ContainingNamespace.ToDisplayString(),
                            ClassName = propertySymbol.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = propertySymbol.Name,
                            Type = propertySymbol.Type.Name,
                            TypeFullName = propertySymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = propertySymbol.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            HasGetter = propertySymbol.GetMethod is not null,
                            HasSetter = propertySymbol.SetMethod is not null,
                            Implements = propertySymbol.ExplicitInterfaceImplementations.Select(i => i.ToDisplayString()).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                        if (propertySymbol.GetMethod is not null)
                        {
                            var getterMethod = CreateMethodFromSymbol(propertySymbol.GetMethod, model, sourceFilePath, fileName, path, lineNumber, columnNumber);
                            sourceMethods.Add(getterMethod);    
                        }
                        if (propertySymbol.SetMethod is not null)
                        {
                            var setterMethod = CreateMethodFromSymbol(propertySymbol.SetMethod, model, sourceFilePath, fileName, path, lineNumber, columnNumber);
                            sourceMethods.Add(setterMethod);
                        }
                    }
                }
            }

            // VB property declarations
            if (vbPropertyDeclarations is not null)
            {
                foreach(var propertyDeclaration in vbPropertyDeclarations)
                {
                    var modifiers = propertyDeclaration.Modifiers;
                    var propertySymbol = model.GetDeclaredSymbol(propertyDeclaration);
                    var codeSpan = propertyDeclaration.SyntaxTree.GetLineSpan(propertyDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (propertySymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = propertySymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = propertySymbol.MetadataToken;
                        }
                        properties.Add(new PropertyInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = propertySymbol.ContainingAssembly.ToDisplayString(),
                            Module = propertySymbol.ContainingModule.ToDisplayString(),
                            Namespace = propertySymbol.ContainingNamespace.ToDisplayString(),
                            ClassName = propertySymbol.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = propertySymbol.Name,
                            Type = propertySymbol.Type.Name,
                            TypeFullName = propertySymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = propertySymbol.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            HasGetter = propertySymbol.GetMethod is not null,
                            HasSetter = propertySymbol.SetMethod is not null,
                            Implements = propertySymbol.ExplicitInterfaceImplementations.Select(i => i.ToDisplayString()).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                        if (propertySymbol.GetMethod is not null)
                        {
                            var getterMethod = CreateMethodFromSymbol(propertySymbol.GetMethod, model, sourceFilePath, fileName, path, lineNumber, columnNumber);
                            sourceMethods.Add(getterMethod);
                        }

                        if (propertySymbol.SetMethod is not null)
                        {
                            var setterMethod = CreateMethodFromSymbol(propertySymbol.SetMethod, model, sourceFilePath, fileName, path, lineNumber, columnNumber);
                            sourceMethods.Add(setterMethod);
                        }
                    }
                }
            }

            // C# field declarations
            if (csFieldDeclarations is not null)
            {
                foreach(var fieldDeclaration in csFieldDeclarations)
                {
                    var modifiers = fieldDeclaration.Modifiers;
                    var variables = fieldDeclaration.Declaration.Variables;
                    var type = fieldDeclaration.Declaration.Type;
                    var typeSymbol = model.GetSymbolInfo(type).Symbol as ITypeSymbol;
                    // Get inheritance information for the containing type
                    INamedTypeSymbol? containingTypeSymbol = null;
                    if (fieldDeclaration.Parent is not null)
                    {
                        containingTypeSymbol = model.GetDeclaredSymbol(fieldDeclaration.Parent) as INamedTypeSymbol;
                    }
                    var baseType = containingTypeSymbol?.BaseType?.ToDisplayString();
                    var implementedInterfaces = containingTypeSymbol?.AllInterfaces.Select(i => i.ToDisplayString()).ToList();
                    
                    foreach(var variable in variables)
                    {
                        var codeSpan = variable.SyntaxTree.GetLineSpan(variable.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;
                        var metadataToken = 0;
                        fields.Add(new FieldInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = model.Compilation.Assembly.ToDisplayString(),
                            Module = model.Compilation.Assembly.Modules.FirstOrDefault()?.ToDisplayString(),
                            Namespace = containingTypeSymbol?.ContainingNamespace?.ToDisplayString() ?? model.Compilation.Assembly.Name,
                            ClassName = GetContainingTypeName(fieldDeclaration),
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = variable.Identifier.Text,
                            Type = typeSymbol?.Name ?? type.ToString(),
                            TypeFullName = typeSymbol?.ToDisplayString() ?? type.ToString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = fieldDeclaration.AttributeLists.SelectMany(al => 
                                al.Attributes.Select(attr => {
                                    var attrSymbol = model.GetSymbolInfo(attr).Symbol;
                                    return new CustomAttributeInfo {
                                        Name = attrSymbol?.ContainingType.Name,
                                        FullName = attrSymbol?.ContainingType.ToDisplayString(),
                                        ConstructorArguments = attr.ArgumentList?.Arguments.Select(arg => arg.Expression.ToString()).ToList() ?? new List<string>(),
                                        NamedArguments = new List<NamedArgumentInfo>()
                                    };
                                })).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB field declarations
            if (vbFieldDeclarations is not null)
            {
                foreach(var fieldDeclaration in vbFieldDeclarations)
                {
                    var modifiers = fieldDeclaration.Modifiers;
                    var variables = fieldDeclaration.Declarators;
                    var firstVariable = variables.FirstOrDefault();
                    var asClause = firstVariable?.AsClause as SimpleAsClauseSyntax;
                    var type = asClause?.Type;
                    var typeSymbol = type is not null ? model.GetSymbolInfo(type).Symbol as ITypeSymbol : null;
                    // Get inheritance information for the containing type
                    INamedTypeSymbol? containingTypeSymbol = null;
                    if (fieldDeclaration.Parent is not null)
                    {
                        if (model is not null)
                            containingTypeSymbol = model.GetDeclaredSymbol(fieldDeclaration.Parent) as INamedTypeSymbol;
                    }
                    var baseType = containingTypeSymbol?.BaseType?.Name;
                    var implementedInterfaces = containingTypeSymbol?.AllInterfaces.Select(i => i.Name).ToList();
                    if (model is not null)
                    {
                        foreach(var variable in variables)
                        {
                            var codeSpan = variable.SyntaxTree.GetLineSpan(variable.Span);
                            var lineNumber = codeSpan.StartLinePosition.Line + 1;
                            var columnNumber = codeSpan.Span.Start.Character + 1;
                            var metadataToken = 0;
                            fields.Add(new FieldInfo
                            {
                                Path = Path.GetRelativePath(path, sourceFilePath),
                                FileName = fileName,
                                Assembly = model?.Compilation.Assembly.ToDisplayString(),
                                Module = model?.Compilation.Assembly.Modules.FirstOrDefault()?.ToDisplayString(),
                                Namespace = containingTypeSymbol?.ContainingNamespace?.ToDisplayString() ?? model?.Compilation.Assembly.Name,
                                ClassName = GetContainingTypeNameVb(fieldDeclaration), // Use VB-specific method
                                Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                                Name = variable.Names.FirstOrDefault()?.Identifier.Text ?? "",
                                Type = typeSymbol?.Name ?? type?.ToString() ?? "Unknown",
                                TypeFullName = typeSymbol?.ToDisplayString() ?? type?.ToString(),
                                LineNumber = lineNumber,
                                ColumnNumber = columnNumber,
                                CustomAttributes = fieldDeclaration.AttributeLists.SelectMany(al => 
                                    al.Attributes.Select(attr => {
                                        var attrSymbol = model.GetSymbolInfo(attr).Symbol;
                                        return new CustomAttributeInfo {
                                            Name = attrSymbol?.ContainingType.Name,
                                            FullName = attrSymbol?.ContainingType.ToDisplayString(),
                                            ConstructorArguments = attr.ArgumentList?.Arguments.Select(arg => arg.GetExpression().ToString()).ToList() ?? new List<string>(),
                                            NamedArguments = new List<NamedArgumentInfo>()
                                        };
                                    })).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                                MetadataToken = metadataToken
                            });
                        }
                    }
                }
            }

            // C# event declarations
            if (csEventDeclarations is not null)
            {
                foreach(var eventDeclaration in csEventDeclarations)
                {
                    var modifiers = eventDeclaration.Modifiers;
                    var eventSymbol = model.GetDeclaredSymbol(eventDeclaration);
                    var codeSpan = eventDeclaration.SyntaxTree.GetLineSpan(eventDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (eventSymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = eventSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(eventSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = eventSymbol.MetadataToken;
                        }
                        events.Add(new EventInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = eventSymbol.ContainingAssembly.ToDisplayString(),
                            Module = eventSymbol.ContainingModule.ToDisplayString(),
                            Namespace = eventSymbol.ContainingNamespace.ToDisplayString(),
                            ClassName = eventSymbol.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = eventSymbol.Name,
                            Type = eventSymbol.Type.Name,
                            TypeFullName = eventSymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = eventSymbol.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString()
                                    }).ToList()
                                }).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // C# event field declarations
            if (csEventFieldDeclarations is not null)
            {
                foreach(var eventFieldDeclaration in csEventFieldDeclarations)
                {
                    var modifiers = eventFieldDeclaration.Modifiers;
                    var variables = eventFieldDeclaration.Declaration.Variables;
                    var type = eventFieldDeclaration.Declaration.Type;
                    var typeSymbol = model.GetSymbolInfo(type).Symbol as ITypeSymbol;
                    
                    foreach(var variable in variables)
                    {
                        var variableSymbol = model.GetDeclaredSymbol(variable) as IFieldSymbol;
                        var codeSpan = variable.SyntaxTree.GetLineSpan(variable.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;
                        // Get inheritance information
                        var containingType = variableSymbol?.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (variableSymbol is not null && SymbolEqualityComparer.Default.Equals(variableSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = variableSymbol.MetadataToken;
                        }
                        events.Add(new EventInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = variableSymbol?.ContainingAssembly.ToDisplayString() ?? model?.Compilation.Assembly.ToDisplayString(),
                            Module = variableSymbol?.ContainingModule.ToDisplayString() ?? model?.Compilation.Assembly.Modules.FirstOrDefault()?.ToDisplayString(),
                            Namespace = variableSymbol?.ContainingNamespace.ToDisplayString() ?? model?.Compilation.Assembly.Name,
                            ClassName = variableSymbol?.ContainingType.Name ?? GetContainingTypeName(eventFieldDeclaration),
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = variable.Identifier.Text,
                            Type = typeSymbol?.Name ?? type.ToString(),
                            TypeFullName = typeSymbol?.ToDisplayString() ?? type.ToString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = eventFieldDeclaration.AttributeLists.SelectMany(al => 
                                al.Attributes.Select(attr => {
                                    var attrSymbol = model.GetSymbolInfo(attr).Symbol;
                                    return new CustomAttributeInfo {
                                        Name = attrSymbol?.ContainingType.Name,
                                        FullName = attrSymbol?.ContainingType.ToDisplayString(),
                                        ConstructorArguments = attr.ArgumentList?.Arguments.Select(arg => arg.Expression.ToString()).ToList() ?? new List<string>(),
                                        NamedArguments = new List<NamedArgumentInfo>()
                                    };
                                })).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB event declarations
            if (vbEventDeclarations is not null)
            {
                foreach(var eventDeclaration in vbEventDeclarations)
                {
                    var modifiers = eventDeclaration.Modifiers;
                    var eventSymbol = model.GetDeclaredSymbol(eventDeclaration);
                    var codeSpan = eventDeclaration.SyntaxTree.GetLineSpan(eventDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (eventSymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = eventSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(eventSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = eventSymbol.MetadataToken;
                        }
                        events.Add(new EventInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = eventSymbol.ContainingAssembly.ToDisplayString(),
                            Module = eventSymbol.ContainingModule.ToDisplayString(),
                            Namespace = eventSymbol.ContainingNamespace.ToDisplayString(),
                            ClassName = eventSymbol.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = eventSymbol.Name,
                            Type = eventSymbol.Type.Name,
                            TypeFullName = eventSymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = eventSymbol.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // C# constructor declarations
            if (csConstructorDeclarations is not null)
            {
                foreach(var constructorDeclaration in csConstructorDeclarations)
                {
                    var modifiers = constructorDeclaration.Modifiers;
                    var constructorSymbol = model.GetDeclaredSymbol(constructorDeclaration);
                    var codeSpan = constructorDeclaration.SyntaxTree.GetLineSpan(constructorDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;
                    var metadataToken = 0;
                    if (constructorSymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = constructorSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        if (SymbolEqualityComparer.Default.Equals(constructorSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = constructorSymbol.MetadataToken;
                        }
                        var isGenericMethod = constructorSymbol.IsGenericMethod;
                        List<string> genericParameters = new List<string>();
                        if (constructorSymbol.ContainingType?.IsGenericType ?? false)
                        {
                            genericParameters = constructorSymbol.ContainingType.TypeParameters.Select(tp => tp.Name).ToList();
                        }
                        constructors.Add(new ConstructorInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = constructorSymbol.ContainingAssembly.ToDisplayString(),
                            Module = constructorSymbol.ContainingModule.ToDisplayString(),
                            Namespace = constructorSymbol.ContainingNamespace.ToDisplayString(),
                            ClassName = containingType?.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = containingType?.Name,
                            ReturnType = "Void",
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            IsGenericMethod = isGenericMethod,
                            GenericParameters = genericParameters,
                            Parameters = constructorSymbol.Parameters.Select(p => new Parameter
                            {
                                Name = p.Name,
                                Type = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Type.ToString()!),
                                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                IsGenericParameter = p.Type is ITypeParameterSymbol
                            }).ToList(),
                            CustomAttributes = constructorSymbol.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            IsStatic = constructorSymbol.IsStatic,
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB constructor declarations
            if (vbConstructorDeclarations is not null)
            {
                foreach(var constructorDeclaration in vbConstructorDeclarations)
                {
                    var ctorStmt = constructorDeclaration.SubNewStatement;
                    if (ctorStmt is not null)
                    {
                        var modifiers = ctorStmt.Modifiers;
                        var constructorSymbol = model.GetDeclaredSymbol(ctorStmt);
                        var codeSpan = ctorStmt.SyntaxTree.GetLineSpan(ctorStmt.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;

                        if (constructorSymbol is not null)
                        {
                            // Get inheritance information
                            var containingType = constructorSymbol.ContainingType;
                            var baseType = containingType?.BaseType?.Name;
                            var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                            var metadataToken = 0;
                            if (SymbolEqualityComparer.Default.Equals(constructorSymbol.ContainingAssembly, model?.Compilation.Assembly))
                            {
                                metadataToken = constructorSymbol.MetadataToken;
                            }
                            constructors.Add(new ConstructorInfo
                            {
                                Path = Path.GetRelativePath(path, sourceFilePath),
                                FileName = fileName,
                                Assembly = constructorSymbol.ContainingAssembly.ToDisplayString(),
                                Module = constructorSymbol.ContainingModule.ToDisplayString(),
                                Namespace = constructorSymbol.ContainingNamespace.ToDisplayString(),
                                ClassName = constructorSymbol.ContainingType.Name,
                                Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                                Name = constructorSymbol.ContainingType.Name,
                                ReturnType = "Void",
                                LineNumber = lineNumber,
                                ColumnNumber = columnNumber,
                                Parameters = constructorSymbol.Parameters.Select(p => new Parameter {
                                    Name = p.Name,
                                    Type = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Type.ToString()!)
                                }).ToList(),
                                CustomAttributes = constructorSymbol.GetAttributes().Select(attr => 
                                    new CustomAttributeInfo {
                                        Name = attr.AttributeClass?.Name,
                                        FullName = attr.AttributeClass?.ToDisplayString(),
                                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                            Name = na.Key,
                                            Value = na.Value.Value?.ToString() ?? string.Empty
                                        }).ToList()
                                    }).ToList(),
                                IsStatic = constructorSymbol.IsStatic,
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                                MetadataToken = metadataToken
                            });
                        }
                    }
                }
            }

            // using declarations
            if (csUsingDirectives is not null)
            {
                foreach(var usingDirective in csUsingDirectives)
                {
                    var name = usingDirective.Name?.ToFullString();
                    var namespaceType = usingDirective.NamespaceOrType.ToFullString();
                    var location = usingDirective.GetLocation().GetLineSpan().StartLinePosition;
                    var lineNumber = location.Line + 1;
                    var columnNumber = location.Character + 1;
                    var namespaceMembers = new List<string>();
                    var assembly = string.Empty;
                    var module = string.Empty;

                    if (usingDirective.Name is not null)
                    {
                        var nameInfo = model.GetSymbolInfo(usingDirective.Name);
                        INamespaceSymbol? nsSymbol = null;

                        if (nameInfo.Symbol is not null and INamespaceSymbol)
                        {
                            nsSymbol = (INamespaceSymbol)nameInfo.Symbol;
                        }
                        else if (nameInfo.CandidateSymbols.Length > 0)
                        {
                            nsSymbol = (INamespaceSymbol)nameInfo.CandidateSymbols.First();
                        }

                        if (nsSymbol is not null)
                        {
                            var nsMembers = nsSymbol.GetNamespaceMembers();
                            namespaceMembers.AddRange(nsMembers.Select(m => m.Name));
                            assembly = nsSymbol.ContainingAssembly?.ToDisplayString();
                            module = nsSymbol.ContainingModule?.ToDisplayString();
                            namespaceType = nsSymbol.ContainingNamespace?.ToDisplayString();
                        }
                    }

                    allUsingDirectives.Add(new Dependency
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        Assembly = assembly,
                        Module = module,
                        Namespace = namespaceType,
                        Name = name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        NamespaceMembers = namespaceMembers
                    });
                }
            }

            // import declarations
            if (vbImportsDirectives is not null)
            {
                foreach(var importDirective in vbImportsDirectives)
                {
                    var name = importDirective.Name?.ToFullString().Trim();
                    var namespaceType = importDirective.Alias?.ToFullString();
                    var location = importDirective.GetLocation().GetLineSpan().StartLinePosition;
                    var lineNumber = location.Line + 1;
                    var columnNumber = location.Character + 1;
                    var namespaceMembers = new List<string>();
                    var assembly = "";
                    var module = "";

                    if (importDirective.Name is not null)
                    {
                        var nameInfo = model.GetSymbolInfo(importDirective.Name);
                        INamespaceSymbol? nsSymbol = null;
                        if (nameInfo.Symbol is not null and INamespaceSymbol)
                        {
                            nsSymbol = (INamespaceSymbol)nameInfo.Symbol;
                        }
                        else if (nameInfo.CandidateSymbols.Length > 0)
                        {
                            nsSymbol = (INamespaceSymbol)nameInfo.CandidateSymbols.First();
                        }
                        if (nsSymbol is not null)
                        {
                            var nsMembers = nsSymbol.GetNamespaceMembers();
                            namespaceMembers.AddRange(nsMembers.Select(m => m.Name));
                            assembly = nsSymbol.ContainingAssembly?.ToDisplayString();
                            module = nsSymbol.ContainingModule?.ToDisplayString();
                            namespaceType = nsSymbol.ContainingNamespace?.ToDisplayString();
                        }
                    }

                    allUsingDirectives.Add(new Dependency
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        Assembly = assembly,
                        Module = module,
                        Namespace = namespaceType,
                        Name = name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        NamespaceMembers = namespaceMembers
                    });
                }
            }

            // method calls
            if (csMethodCalls is not null)
            {
                foreach(var methodCall in csMethodCalls)
                {
                    if (model is not null)
                        TrackCsMethodCall(methodCall, model, allMethodCalls, path, sourceFilePath, fileName);
                }
            }

            if (vbMethodCalls is not null)
            {
                foreach(var methodCall in vbMethodCalls)
                {
                    if (model is not null)
                        TrackVbMethodCall(methodCall, model, allMethodCalls, path, sourceFilePath, fileName);
                }
            }
        }

        // Process F# files
        var (fsharpMethods, fsharpDependencies, fsharpMethodCalls) = GetFSharpMethods(path);
        sourceMethods.AddRange(fsharpMethods);
        allUsingDirectives.AddRange(fsharpDependencies);
        allMethodCalls.AddRange(fsharpMethodCalls);
        // 1. Create a common, anonymous type for all discovered members.
        var allMembers = sourceMethods.Select(m => new { m.Namespace, m.ClassName, m.Name, m.FileName })
            .Concat(properties.Select(p => new { p.Namespace, p.ClassName, p.Name, p.FileName }))
            .Concat(fields.Select(f => new { f.Namespace, f.ClassName, f.Name, f.FileName }))
            .Concat(events.Select(e => new { e.Namespace, e.ClassName, e.Name, e.FileName }))
            .Concat(constructors.Select(c => new { c.Namespace, c.ClassName, c.Name, c.FileName }));
        // 2. Create unique method nodes efficiently using GroupBy.
        var methodNodes = allMembers
            .Where(m => m.Name is not null)
            .GroupBy(m => CreateNodeId(m.Namespace ?? string.Empty, m.ClassName ?? string.Empty, m.Name!))
            .Select(g => g.First())
            .Select(m => new MethodNode
            {
                Id = CreateNodeId(m.Namespace ?? string.Empty, m.ClassName ?? string.Empty, m.Name!),
                Name = m.Name!,
                ClassName = m.ClassName ?? string.Empty,
                Namespace = m.Namespace ?? string.Empty,
                FileName = m.FileName ?? string.Empty
            })
            .ToList();
        // 3. Create Edges based on tracked method calls using a LINQ pipeline.
        var callEdges = (from call in allMethodCalls
            let callCallerClass = call.CallerClass
            where callCallerClass != null
            let callerMethod = call.CallerMethod
            where callerMethod != null
            let calledMethodName = call.CalledMethod
            where calledMethodName != null
            let value = call.ClassName
            where value != null
            where !string.IsNullOrEmpty(callCallerClass) && !string.IsNullOrEmpty(callerMethod) && !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(calledMethodName)
        let sourceNodeId = CreateNodeId(call.CallerNamespace ?? "", callCallerClass, callerMethod)
        let lastDotIndex = calledMethodName.LastIndexOf('.')
        let targetMemberName = lastDotIndex >= 0 ? calledMethodName.Substring(lastDotIndex + 1) : calledMethodName
        let targetNodeId = CreateNodeId(call.Namespace ?? "", value, targetMemberName)
        where methodNodes.Any(n => n.Id == sourceNodeId)
        select new MethodCallEdge
        {
            SourceId = sourceNodeId,
            TargetId = targetNodeId,
            CallLocation = new CallLocation { FileName = call.FileName, LineNumber = call.LineNumber, ColumnNumber = call.ColumnNumber },
            CalledMethodName = calledMethodName,
            Arguments = call.Arguments ?? [],
            ArgumentExpressions = call.ArgumentExpressions ?? [],
            CallType = call.CallType
        }).ToList();
        var callGraph = new CallGraph
        {
            Edges = callEdges,
            Nodes = methodNodes
        };
        // Source-to-Assembly Mapping Logic
        var assemblyMemberLookup = new Dictionary<string, object>();
        var assemblyNameLookup = new Dictionary<string, object>();
        foreach (var asmMethod in assemblyMethods)
        {
            var asmSignature = asmMethod.AssemblySignature;
            if (asmSignature is not null)
            {
                assemblyMemberLookup.TryAdd(asmSignature, asmMethod);
            }
            assemblyNameLookup.TryAdd($"{asmMethod.Namespace}.{asmMethod.ClassName}.{asmMethod.Name}", asmMethod);
        }

        foreach (var method in sourceMethods)
        {
            AddMapping(method, "Method");
        }
        return (sourceMethods, allUsingDirectives, allMethodCalls, properties, fields, events, constructors, callGraph, sourceAssemblyMappings);

        string CreateNodeId(string ns, string className, string memberName) => $"{ns}.{className}.{memberName}";

        void AddMapping<T>(T sourceMember, string memberType) where T : class
        {
            switch (sourceMember)
            {
                case Method { SourceSignature: not null } sourceMethod:
                {
                    var sourceId = sourceMethod.SourceSignature;
                    var isMapped = assemblyMemberLookup.TryGetValue(sourceMethod.SourceSignature, out var asmMemberObj);
                    if (!isMapped)
                    {
                        isMapped = assemblyNameLookup.TryGetValue($"{sourceMethod.Namespace}.{sourceMethod.ClassName}.{sourceMethod.Name}", out var asmMember);
                        if (isMapped)
                        {
                            asmMemberObj = asmMember;
                        }
                    }
                    if (!isMapped)
                    {
                        break;
                    }
                    string? asmId = null;
                    if (isMapped && asmMemberObj is Method asmMethod)
                    {
                        asmId = asmMethod.AssemblySignature;
                    }
                    sourceAssemblyMappings.Add(new SourceAssemblyMapping
                    {
                        SourceId = sourceId,
                        SourcePath = sourceMethod.Path,
                        SourceLineNumber = sourceMethod.LineNumber,
                        SourceColumnNumber = sourceMethod.ColumnNumber,
                        SourceMetadataToken = sourceMethod.MetadataToken,
                        AssemblyMetadataToken = isMapped ? (asmMemberObj is Method m ? m.MetadataToken : 0) : 0,
                        AssemblyName = sourceMethod.Assembly,
                        ModuleName = sourceMethod.Module,
                        AssemblyId = asmId,
                        MemberType = memberType,
                        MemberName = sourceMethod.Name,
                        ClassName = sourceMethod.ClassName,
                        Namespace = sourceMethod.Namespace,
                        IsMapped = isMapped
                    });
                    break;
                }
            }
        }
    }

    private static string GetContainingTypeName(MemberDeclarationSyntax member)
    {
        var parent = member.Parent;
        while (parent is not null)
        {
            if (parent is ClassDeclarationSyntax classDecl)
                return classDecl.Identifier.Text;
            if (parent is StructDeclarationSyntax structDecl)
                return structDecl.Identifier.Text;
            parent = parent.Parent;
        }
        return "";
    }

    private static void TrackCsMethodCall(InvocationExpressionSyntax methodCall, SemanticModel model, List<MethodCalls> allMethodCalls, string path, string sourceFilePath, string fileName)
    {
        var callArguments = methodCall.ArgumentList;
        var callExpression = methodCall.Expression;
        var location = methodCall.GetLocation().GetLineSpan().StartLinePosition;
        var lineNumber = location.Line + 1;
        var columnNumber = location.Character + 1;
        var callArgsTypes = new List<string>();
        var callArgExpressions = new List<string>();
        foreach (var arg in callArguments.Arguments)
        {
            var argType = model.GetTypeInfo(arg.Expression).Type;
            // Fallback to expression type if we can't get the type info
            callArgsTypes.Add(argType is not null ? argType.ToDisplayString() : arg.Expression.GetType().Name);
            callArgExpressions.Add(arg.Expression.ToFullString());
        }
        var exprInfo = model.GetSymbolInfo(callExpression);
        var calledMethod = string.Empty;
        var isInMetadata = false;
        var isInSource = false;
        var assembly = string.Empty;
        var module = string.Empty;
        var namespaceString = string.Empty;
        var className = string.Empty;
        var callerMethod = string.Empty;
        var callerNamespace = string.Empty;
        var callerClass = string.Empty;
        var callType = CallType.MethodCall;
        
        if (exprInfo.Symbol is not null)
        {
            switch (exprInfo.Symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } propSymbol:
                    callType = propSymbol.MethodKind == MethodKind.PropertyGet ? CallType.PropertyGet : CallType.PropertySet;
                    calledMethod = propSymbol.AssociatedSymbol?.Name ?? propSymbol.Name;
                    className = propSymbol.AssociatedSymbol?.ContainingType?.Name ?? propSymbol.ContainingType?.Name ?? "";
                    namespaceString = propSymbol.AssociatedSymbol?.ContainingNamespace?.ToDisplayString() ??
                                      propSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
                
                case IMethodSymbol { MethodKind: MethodKind.Constructor } ctorSymbol:
                    callType = CallType.ConstructorCall;
                    calledMethod = ctorSymbol.ContainingType?.Name ?? ctorSymbol.Name;
                    className = ctorSymbol.ContainingType?.Name ?? "";
                    namespaceString = ctorSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;

                case IMethodSymbol methodSymbol:
                    calledMethod = methodSymbol.ToDisplayString();
                    isInMetadata = methodSymbol.Locations.Any(l => l.IsInMetadata);
                    isInSource = methodSymbol.Locations.Any(l => l.IsInSource);
                    assembly = methodSymbol.ContainingAssembly?.ToDisplayString() ?? "";
                    module = methodSymbol.ContainingModule?.ToDisplayString() ?? "";
                    namespaceString = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    className = methodSymbol.ContainingType?.Name ?? "";
                    break;

                case IPropertySymbol propertySymbol:
                    callType = CallType.PropertyGet;
                    calledMethod = propertySymbol.Name;
                    className = propertySymbol.ContainingType?.Name ?? "";
                    namespaceString = propertySymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
            }
        }

        // Get caller context
        if (model.GetEnclosingSymbol(methodCall.SpanStart) is IMethodSymbol callerSymbol)
        {
            callerMethod = callerSymbol.Name;
            callerNamespace = callerSymbol.ContainingNamespace.ToDisplayString();
            callerClass = callerSymbol.ContainingType.Name;
        }

        var isInternal = isInSource || !isInMetadata;
        if (assembly == String.Empty && module == String.Empty && namespaceString == String.Empty && className == String.Empty && calledMethod == String.Empty)
        {
            return;
        }
        allMethodCalls.Add(new MethodCalls
        {
            Path = Path.GetRelativePath(path, sourceFilePath),
            FileName = fileName,
            Assembly = assembly,
            Module = module,
            Namespace = namespaceString,
            ClassName = className,
            CalledMethod = calledMethod,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            Arguments = callArgsTypes,
            ArgumentExpressions = callArgExpressions,
            CallType = callType,
            CallerMethod = callerMethod,
            CallerNamespace = callerNamespace,
            CallerClass = callerClass,
            IsInternal = isInternal
        });
    }

    private static void TrackVbMethodCall(Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax methodCall, SemanticModel model, List<MethodCalls> allMethodCalls, string path, string sourceFilePath, string fileName)
    {
        var callArguments = methodCall.ArgumentList;
        var callExpression = methodCall.Expression;
        var location = methodCall.GetLocation().GetLineSpan().StartLinePosition;
        var lineNumber = location.Line + 1;
        var columnNumber = location.Character + 1;
        var fullName = callExpression.ToFullString();
        var callArgsTypes = callArguments?.Arguments.Select(a => a.ToFullString()).ToList();
        var exprInfo = model.GetSymbolInfo(callExpression);
        var calledMethod = string.Empty;
        var isInMetadata = false;
        var isInSource = false;
        var assembly = string.Empty;
        var module = string.Empty;
        var namespaceString = string.Empty;
        var className = string.Empty;
        var callerMethod = string.Empty;
        var callerNamespace = string.Empty;
        var callerClass = string.Empty;

        if (exprInfo.Symbol is not null)
        {
            var methodSymbol = exprInfo.Symbol;
            calledMethod = methodSymbol.ToDisplayString();
            isInMetadata = methodSymbol.Locations.Any(loc => loc.IsInMetadata);
            isInSource = methodSymbol.Locations.Any(loc => loc.IsInSource);
            assembly = methodSymbol.ContainingAssembly.ToDisplayString();
            module = methodSymbol.ContainingModule.ToDisplayString();
            namespaceString = methodSymbol.ContainingNamespace.ToDisplayString();
            className = methodSymbol.ContainingType.ToDisplayString();
        }

        // Get caller context
        if (model.GetEnclosingSymbol(methodCall.SpanStart) is IMethodSymbol callerSymbol)
        {
            callerMethod = callerSymbol.Name;
            callerNamespace = callerSymbol.ContainingNamespace.ToDisplayString();
            callerClass = callerSymbol.ContainingType.Name;
        }

        var isInternal = isInSource || !isInMetadata;

        allMethodCalls.Add(new MethodCalls
        {
            Path = Path.GetRelativePath(path, sourceFilePath),
            FileName = fileName,
            Assembly = assembly,
            Module = module,
            Namespace = namespaceString,
            ClassName = className,
            CalledMethod = calledMethod,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            Arguments = callArgsTypes,
            CallerMethod = callerMethod,
            CallerNamespace = callerNamespace,
            CallerClass = callerClass,
            IsInternal = isInternal
        });
    }

    /// <summary>
    /// Get list of files to inspect for the given path and file extensions
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <param name="fileExtensions">File extensions to search for</param>
    /// <returns>List of files</returns>
    private static List<string> GetFilesToInspect(string path, params string[]? fileExtensions)
    {
        var filesToInspect = new List<string>();
        var fileAttributes = File.GetAttributes(path);
        if (fileExtensions is null || fileExtensions.Length == 0)
        {
            return filesToInspect; // Return empty list if no extensions provided
        }
        if (fileAttributes.HasFlag(FileAttributes.Directory))
        {
            filesToInspect.AddRange(from extension in fileExtensions from inputFile in new DirectoryInfo(path).EnumerateFiles($"*{extension}", SearchOption.AllDirectories) where !inputFile.FullName.EndsWith($".g{extension}") select inputFile.FullName);
        }
        else
        {
            var extension = Path.GetExtension(path);
            // Check if the file extension matches any of the provided extensions
            if (fileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                filesToInspect.Add(path);    
            }
        }
        return filesToInspect;
    }
}