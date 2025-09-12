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
using CompilationUnitSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax;
using FieldDeclarationSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax;
using InvocationExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax;

namespace Depscan;

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
        if (methodSymbol == null)
            return string.Empty;

        var ns = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var className = methodSymbol.ContainingType?.Name ?? "";
        var methodName = methodSymbol.Name;
        var returnType = methodSymbol.MethodKind == MethodKind.Constructor ? "" : (methodSymbol.ReturnType.ToDisplayString());

        var parameters = methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
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

    private static readonly JsonSerializerOptions options = new()
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
        }, options);
    }

    /// <summary>
    /// Get all assembly information for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly information</returns>
    private static List<AssemblyInformation> GetAssemblyInformation(string path)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension);
        var assemblyInformation = new List<AssemblyInformation>();
        var failedAssemblies = new List<string>();

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
                var buildPart = fileVersionInfo.FileBuildPart == null ? string.Empty : $".{fileVersionInfo.FileBuildPart}";
                var privatePart = fileVersionInfo.FilePrivatePart == null ? string.Empty : $".{fileVersionInfo.FilePrivatePart}";
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
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension);
        var assemblyMethods = new List<Method>();
        var failedAssemblies = new List<string>();
        foreach(var assemblyFilePath in assembliesToInspect)
        {
            var fileName = Path.GetFileName(assemblyFilePath);

            try
            {
                var assembly = Assembly.LoadFrom(assemblyFilePath);
                var types = assembly.GetExportedTypes();
                
                foreach(var type in types)
                {
                    // Methods
                    var methods = type.GetMethods();
                    foreach(var method in methods)
                    {
                        var parameters = method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
                        var paramString = string.Join(",", parameters);
                        var returnType = method.ReturnType.FullName ?? method.ReturnType.Name;
                        var methodName = method.Name;
                        var className = method.DeclaringType?.Name ?? "UnknownType";
                        var ns = method.DeclaringType?.Namespace ?? "";
                        var assemblySignature = $"{ns}.{className}.{methodName}({paramString}):{returnType}";
                        var isGenericMethod = method.IsGenericMethod;
                        var isGenericMethodDefinition = method.IsGenericMethodDefinition;
                        List<string> genericParameters = new List<string>();
                        if (method.IsGenericMethodDefinition)
                        {
                            genericParameters = method.GetGenericArguments().Select(t => t.Name).ToList();
                        }
                        if (method.Name == ".ctor" || method.Name == ".cctor")
                        {
                            assemblySignature = $"{ns}.{className}.{methodName}({paramString})";
                        }
                        if ($"{method.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName)
                        {
                            // Get type information for inheritance
                            var typ = method.DeclaringType;
                            var baseType = typ?.BaseType?.Name;
                            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList();

                            assemblyMethods.Add(new Method
                            {
                                Path = assemblyFilePath,
                                FileName = fileName,
                                Module = method.DeclaringType?.Module.ToString(),
                                Namespace = method.DeclaringType?.Namespace,
                                ClassName = typ?.Name ?? string.Empty,
                                Attributes = method.Attributes.ToString(),
                                Name = method.Name,
                                ReturnType = method.ReturnType.FullName ?? method.ReturnType.Name,
                                IsGenericMethod = isGenericMethod,
                                IsGenericMethodDefinition = isGenericMethodDefinition,
                                GenericParameters = genericParameters,
                                Parameters = method.GetParameters().Select(p => new Parameter
                                {
                                    Name = p.Name,
                                    Type = p.ParameterType.FullName ?? p.ParameterType.Name,
                                    TypeFullName = p.ParameterType.FullName ?? p.ParameterType.Name,
                                    IsGenericParameter = p.ParameterType.IsGenericParameter
                                }).ToList(),
                                CustomAttributes = method.GetCustomAttributesData().Select(attr => 
                                    new CustomAttributeInfo {
                                        Name = attr.AttributeType.Name,
                                        FullName = attr.AttributeType.FullName,
                                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                            Name = na.MemberName,
                                            Value = na.TypedValue.Value?.ToString() ?? string.Empty
                                        }).ToList()
                                    }).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                                MetadataToken = method.MetadataToken,
                                AssemblySignature = assemblySignature
                            });
                        }
                    }

                    // Constructors
                    var constructors = type.GetConstructors();
                    foreach(var ctor in constructors)
                    {
                        if ($"{ctor.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName)
                        {
                            // Get type information for inheritance
                            var typ = ctor.DeclaringType;
                            var baseType = typ?.BaseType?.Name;
                            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList();
                            assemblyMethods.Add(new Method
                            {
                                Path = assemblyFilePath,
                                FileName = fileName,
                                Module = ctor.DeclaringType?.Module.ToString(),
                                Namespace = ctor.DeclaringType?.Namespace,
                                ClassName = typ?.Name ?? string.Empty,
                                Attributes = ctor.Attributes.ToString(),
                                Name = ".ctor",
                                ReturnType = "Void",
                                Parameters = ctor.GetParameters().Select(p => new Parameter {
                                    Name = p.Name,
                                    Type = p.ParameterType.FullName
                                }).ToList(),
                                CustomAttributes = ctor.GetCustomAttributesData().Select(attr => 
                                    new CustomAttributeInfo {
                                        Name = attr.AttributeType.Name,
                                        FullName = attr.AttributeType.FullName,
                                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                            Name = na.MemberName,
                                            Value = na.TypedValue.Value?.ToString() ?? string.Empty
                                        }).ToList()
                                    }).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                                MetadataToken = ctor.MetadataToken,
                                AssemblySignature = $"{typ?.Name}"
                            });
                        }
                    }

                    // Properties
                    var properties = type.GetProperties();
                    foreach(var prop in properties)
                    {
                        if ($"{prop.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName)
                        {
                            // Get type information for inheritance
                            var typ = prop.DeclaringType;
                            var baseType = typ?.BaseType?.Name;
                            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList();

                            assemblyMethods.Add(new Method
                            {
                                Path = assemblyFilePath,
                                FileName = fileName,
                                Module = prop.DeclaringType?.Module.ToString(),
                                Namespace = prop.DeclaringType?.Namespace,
                                ClassName = typ?.Name ?? string.Empty,
                                Attributes = "Property",
                                Name = prop.Name,
                                ReturnType = prop.PropertyType.Name,
                                Parameters = [],
                                CustomAttributes = prop.GetCustomAttributesData().Select(attr => 
                                    new CustomAttributeInfo {
                                        Name = attr.AttributeType.Name,
                                        FullName = attr.AttributeType.FullName,
                                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                            Name = na.MemberName,
                                            Value = na.TypedValue.Value?.ToString() ?? string.Empty
                                        }).ToList()
                                    }).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                            });
                        }
                    }

                    // Fields
                    var fields = type.GetFields();
                    foreach(var field in fields)
                    {
                        if ($"{field.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName)
                        {
                            // Get type information for inheritance
                            var typ = field.DeclaringType;
                            var baseType = typ?.BaseType?.Name;
                            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList();

                            assemblyMethods.Add(new Method
                            {
                                Path = assemblyFilePath,
                                FileName = fileName,
                                Module = field.DeclaringType?.Module.ToString(),
                                Namespace = field.DeclaringType?.Namespace,
                                ClassName = typ?.Name ?? string.Empty,
                                Attributes = field.Attributes.ToString(),
                                Name = field.Name,
                                ReturnType = field.FieldType.Name,
                                Parameters = [],
                                CustomAttributes = field.GetCustomAttributesData().Select(attr => 
                                    new CustomAttributeInfo {
                                        Name = attr.AttributeType.Name,
                                        FullName = attr.AttributeType.FullName,
                                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                            Name = na.MemberName,
                                            Value = na.TypedValue.Value?.ToString() ?? string.Empty
                                        }).ToList()
                                    }).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces
                            });
                        }
                    }

                    // Events
                    var events = type.GetEvents();
                    foreach(var evt in events)
                    {
                        if ($"{evt.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName)
                        {
                            // Get type information for inheritance
                            var typ = evt.DeclaringType;
                            var baseType = typ?.BaseType?.Name;
                            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList();

                            assemblyMethods.Add(new Method
                            {
                                Path = assemblyFilePath,
                                FileName = fileName,
                                Module = evt.DeclaringType?.Module.ToString(),
                                Namespace = evt.DeclaringType?.Namespace,
                                ClassName = typ?.Name ?? string.Empty,
                                Attributes = evt.Attributes.ToString(),
                                Name = evt.Name,
                                ReturnType = evt.EventHandlerType?.Name ?? string.Empty,
                                Parameters = [],
                                CustomAttributes = evt.GetCustomAttributesData().Select(attr => 
                                    new CustomAttributeInfo {
                                        Name = attr.AttributeType.Name,
                                        FullName = attr.AttributeType.FullName,
                                        ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                        NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                            Name = na.MemberName,
                                            Value = na.TypedValue.Value?.ToString() ?? string.Empty
                                        }).ToList()
                                    }).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces
                            });
                        }
                    }
                }
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

        return assemblyMethods;
    }

    private static string GetContainingTypeNameVB(Microsoft.CodeAnalysis.VisualBasic.Syntax.FieldDeclarationSyntax member)
    {
        var parent = member.Parent;
        while (parent != null)
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
                var lines = fileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                string currentModule = "Global";
                string currentType = "";
                
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    var lineNumber = i + 1;
                    
                    // Extract module declarations
                    var moduleMatch = Regex.Match(line, @"^\s*module\s+([\w\.]+)");
                    if (moduleMatch.Success)
                    {
                        currentModule = moduleMatch.Groups[1].Value;
                    }
                    
                    // Extract type declarations
                    var typeMatch = Regex.Match(line, @"^\s*type\s+(\w+)");
                    if (typeMatch.Success)
                    {
                        currentType = typeMatch.Groups[1].Value;
                    }
                    
                    // Extract function declarations: "let functionName" or "let rec functionName"
                    var functionMatch = Regex.Match(line, @"^\s*let\s+(rec\s+)?(\w+)");
                    if (functionMatch.Success)
                    {
                        var functionName = functionMatch.Groups[2].Value;
                        
                        // Skip common F# keywords that might match the pattern
                        if (functionName == "rec" || functionName == "in" || functionName == "and") 
                            continue;
                        
                        // Determine the containing context
                        string containingContext = currentModule;
                        if (!string.IsNullOrEmpty(currentType))
                            containingContext = currentType;
                        
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
                            Parameters = new List<Parameter>(),
                            CustomAttributes = new List<CustomAttributeInfo>()
                        });
                    }
                    
                    // Extract member declarations: "member this.MemberName" or "member _.MemberName"
                    var memberMatch = Regex.Match(line, @"^\s*member\s+(\w+|\.)\.(\w+)");
                    if (memberMatch.Success)
                    {
                        var instanceName = memberMatch.Groups[1].Value;
                        var memberName = memberMatch.Groups[2].Value;
                        
                        string containingType = currentType;
                        if (string.IsNullOrEmpty(containingType))
                            containingType = "Unknown";
                        
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
                            Parameters = new List<Parameter>(),
                            CustomAttributes = new List<CustomAttributeInfo>()
                        });
                    }
                    
                    // Extract constructor declarations: "new(args) ="
                    var constructorMatch = Regex.Match(line, @"^\s*new\s*\(");
                    if (constructorMatch.Success)
                    {
                        string containingType = currentType;
                        if (string.IsNullOrEmpty(containingType))
                            containingType = "Unknown";
                        
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
                            Parameters = new List<Parameter>(),
                            CustomAttributes = new List<CustomAttributeInfo>()
                        });
                    }
                }
                
                // Extract dependencies (open statements)
                var openPattern = @"^\s*open\s+([\w\.]+)";
                var openMatches = Regex.Matches(fileContent, openPattern, RegexOptions.Multiline);
                
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
                        NamespaceMembers = new List<string>()
                    });
                }
                
                // Extract method calls (function calls with parentheses)
                var callPattern = @"(\w+)\s*\(";
                var callMatches = Regex.Matches(fileContent, callPattern);
                
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
                        Arguments = new List<string>()
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

    /// <summary>
    /// Get all source methods for the given path to C# source or directory of C# source
    /// </summary>
    /// <param name="path">Filesystem path to C# source file or directory containing C# source files</param>
    /// <param name="assemblyMethods">List of assembly methods</param>
    /// <returns>Tuple with List of source methods and using directives</returns>
    private static (List<Method>, List<Dependency>, List<MethodCalls>, List<PropertyInfo>, List<FieldInfo>, List<EventInfo>, List<ConstructorInfo>, CallGraph, List<SourceAssemblyMapping>) GetSourceMethods(string path, List<Method> assemblyMethods)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension);
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
        var callEdges = new List<MethodCallEdge>();
        var methodNodes = new List<MethodNode>();
        var sourceAssemblyMappings = new List<SourceAssemblyMapping>();
#pragma warning disable IL3000
        var Mscorlib = MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, typeof(object).Assembly.Location));
#pragma warning restore IL3000
        var metadataReferences = new List<PortableExecutableReference>
        {
            Mscorlib
        };
        foreach (var externalAssembly in assembliesToInspect)
        {
            metadataReferences.Add(MetadataReference.CreateFromFile(externalAssembly));
        }

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
                var compilation = CSharpCompilation.Create(fileName, syntaxTrees: new[] { tree }, references: metadataReferences);
                model = compilation.GetSemanticModel(tree);
            }
            else if (extn.Equals(Constants.VBSourceExtension))
            {
                var tree = (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(fileContent);
                vbRoot = tree.GetCompilationUnitRoot();
                var compilation = VisualBasicCompilation.Create(fileName, syntaxTrees: new[] { tree }, references: metadataReferences);
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

            // C# method declarations - modify this section
            if (csMethodDeclarations != null)
            {
                foreach(var methodDeclaration in csMethodDeclarations)
                {
                    var modifiers = methodDeclaration.Modifiers;
                    var methodSymbol = model.GetDeclaredSymbol(methodDeclaration);
                    var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (methodSymbol != null)
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
                        List<string> genericParameters = new List<string>();
                        if (methodSymbol.TypeParameters != null)
                        {
                            genericParameters = methodSymbol.TypeParameters.Select(tp => tp.Name).ToList();
                        }
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
                                TypeFullName = p.Type.ToDisplayString(),
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
            if (vbMethodDeclarations != null)
            {
                foreach(var methodDeclaration in vbMethodDeclarations)
                {
                    var modifiers = methodDeclaration.Modifiers;
                    var method = model.GetDeclaredSymbol(methodDeclaration);
                    var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (method != null)
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
                                Type = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Type.ToString()!)
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
            if (csPropertyDeclarations != null)
            {
                foreach(var propertyDeclaration in csPropertyDeclarations)
                {
                    var modifiers = propertyDeclaration.Modifiers;
                    var property = model.GetDeclaredSymbol(propertyDeclaration);
                    var codeSpan = propertyDeclaration.SyntaxTree.GetLineSpan(propertyDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (property != null)
                    {
                        // Get inheritance information
                        var containingType = property.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(property.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = property.MetadataToken;
                        }
                        properties.Add(new PropertyInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = property.ContainingAssembly.ToDisplayString(),
                            Module = property.ContainingModule.ToDisplayString(),
                            Namespace = property.ContainingNamespace.ToDisplayString(),
                            ClassName = property.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = property.Name,
                            Type = property.Type.Name,
                            TypeFullName = property.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = property.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            HasGetter = property.GetMethod != null,
                            HasSetter = property.SetMethod != null,
                            Implements = property.ExplicitInterfaceImplementations.Select(i => i.ToDisplayString()).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB property declarations
            if (vbPropertyDeclarations != null)
            {
                foreach(var propertyDeclaration in vbPropertyDeclarations)
                {
                    var modifiers = propertyDeclaration.Modifiers;
                    var property = model.GetDeclaredSymbol(propertyDeclaration);
                    var codeSpan = propertyDeclaration.SyntaxTree.GetLineSpan(propertyDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (property != null)
                    {
                        // Get inheritance information
                        var containingType = property.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = containingType?.AllInterfaces.Select(i => i.Name).ToList();
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(property.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = property.MetadataToken;
                        }
                        properties.Add(new PropertyInfo
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Assembly = property.ContainingAssembly.ToDisplayString(),
                            Module = property.ContainingModule.ToDisplayString(),
                            Namespace = property.ContainingNamespace.ToDisplayString(),
                            ClassName = property.ContainingType.Name,
                            Attributes = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(string.Join(", ", modifiers)),
                            Name = property.Name,
                            Type = property.Type.Name,
                            TypeFullName = property.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = property.GetAttributes().Select(attr => 
                                new CustomAttributeInfo {
                                    Name = attr.AttributeClass?.Name,
                                    FullName = attr.AttributeClass?.ToDisplayString(),
                                    ConstructorArguments = attr.ConstructorArguments.Select(arg => arg.Value?.ToString() ?? string.Empty).ToList(),
                                    NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo {
                                        Name = na.Key,
                                        Value = na.Value.Value?.ToString() ?? string.Empty
                                    }).ToList()
                                }).ToList(),
                            HasGetter = property.GetMethod != null,
                            HasSetter = property.SetMethod != null,
                            Implements = property.ExplicitInterfaceImplementations.Select(i => i.ToDisplayString()).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // C# field declarations
            if (csFieldDeclarations != null)
            {
                foreach(var fieldDeclaration in csFieldDeclarations)
                {
                    var modifiers = fieldDeclaration.Modifiers;
                    var variables = fieldDeclaration.Declaration.Variables;
                    var type = fieldDeclaration.Declaration.Type;
                    var typeSymbol = model.GetSymbolInfo(type).Symbol as ITypeSymbol;
                    // Get inheritance information for the containing type
                    INamedTypeSymbol? containingTypeSymbol = null;
                    if (fieldDeclaration.Parent != null)
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
            if (vbFieldDeclarations != null)
            {
                foreach(var fieldDeclaration in vbFieldDeclarations)
                {
                    var modifiers = fieldDeclaration.Modifiers;
                    var variables = fieldDeclaration.Declarators;
                    var firstVariable = variables.FirstOrDefault();
                    var asClause = firstVariable?.AsClause as SimpleAsClauseSyntax;
                    var type = asClause?.Type;
                    var typeSymbol = type != null ? model.GetSymbolInfo(type).Symbol as ITypeSymbol : null;
                    // Get inheritance information for the containing type
                    INamedTypeSymbol? containingTypeSymbol = null;
                    if (fieldDeclaration.Parent != null)
                    {
                        if (model != null)
                            containingTypeSymbol = model.GetDeclaredSymbol(fieldDeclaration.Parent) as INamedTypeSymbol;
                    }
                    var baseType = containingTypeSymbol?.BaseType?.Name;
                    var implementedInterfaces = containingTypeSymbol?.AllInterfaces.Select(i => i.Name).ToList();
                    if (model != null)
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
                                ClassName = GetContainingTypeNameVB(fieldDeclaration), // Use VB-specific method
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
            if (csEventDeclarations != null)
            {
                foreach(var eventDeclaration in csEventDeclarations)
                {
                    var modifiers = eventDeclaration.Modifiers;
                    var eventSymbol = model.GetDeclaredSymbol(eventDeclaration);
                    var codeSpan = eventDeclaration.SyntaxTree.GetLineSpan(eventDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (eventSymbol != null)
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
            if (csEventFieldDeclarations != null)
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
                        if (variableSymbol != null && SymbolEqualityComparer.Default.Equals(variableSymbol.ContainingAssembly, model?.Compilation.Assembly))
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
            if (vbEventDeclarations != null)
            {
                foreach(var eventDeclaration in vbEventDeclarations)
                {
                    var modifiers = eventDeclaration.Modifiers;
                    var eventSymbol = model.GetDeclaredSymbol(eventDeclaration);
                    var codeSpan = eventDeclaration.SyntaxTree.GetLineSpan(eventDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (eventSymbol != null)
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
            if (csConstructorDeclarations != null)
            {
                foreach(var constructorDeclaration in csConstructorDeclarations)
                {
                    var modifiers = constructorDeclaration.Modifiers;
                    var constructorSymbol = model.GetDeclaredSymbol(constructorDeclaration);
                    var codeSpan = constructorDeclaration.SyntaxTree.GetLineSpan(constructorDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;
                    var metadataToken = 0;
                    if (constructorSymbol != null)
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
                                TypeFullName = p.Type.ToDisplayString(),
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
            if (vbConstructorDeclarations != null)
            {
                foreach(var constructorDeclaration in vbConstructorDeclarations)
                {
                    var ctorStmt = constructorDeclaration.SubNewStatement;
                    if (ctorStmt != null)
                    {
                        var modifiers = ctorStmt.Modifiers;
                        var constructorSymbol = model.GetDeclaredSymbol(ctorStmt);
                        var codeSpan = ctorStmt.SyntaxTree.GetLineSpan(ctorStmt.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;

                        if (constructorSymbol != null)
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
            if (csUsingDirectives != null)
            {
                foreach(var usingDirective in csUsingDirectives)
                {
                    var name = usingDirective.Name?.ToFullString();
                    var namespaceType = usingDirective.NamespaceOrType.ToFullString();
                    var location = usingDirective.GetLocation().GetLineSpan().StartLinePosition;
                    var lineNumber = location.Line + 1;
                    var columnNumber = location.Character + 1;
                    var namespaceMembers = new List<string>();
                    var Assembly = string.Empty;
                    var Module = string.Empty;

                    if (usingDirective.Name != null)
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

                        if (nsSymbol != null)
                        {
                            var nsMembers = nsSymbol.GetNamespaceMembers();
                            namespaceMembers.AddRange(nsMembers.Select(m => m.Name));
                            Assembly = nsSymbol.ContainingAssembly?.ToDisplayString();
                            Module = nsSymbol.ContainingModule?.ToDisplayString();
                            namespaceType = nsSymbol.ContainingNamespace?.ToDisplayString();
                        }
                    }

                    allUsingDirectives.Add(new Dependency
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        Assembly = Assembly,
                        Module = Module,
                        Namespace = namespaceType,
                        Name = name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        NamespaceMembers = namespaceMembers
                    });
                }
            }

            // import declarations
            if (vbImportsDirectives != null)
            {
                foreach(var importDirective in vbImportsDirectives)
                {
                    var name = importDirective.Name?.ToFullString().Trim();
                    var namespaceType = importDirective.Alias?.ToFullString();
                    var location = importDirective.GetLocation().GetLineSpan().StartLinePosition;
                    var lineNumber = location.Line + 1;
                    var columnNumber = location.Character + 1;
                    var namespaceMembers = new List<string>();
                    var Assembly = "";
                    var Module = "";

                    if (importDirective.Name != null)
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
                        if (nsSymbol != null)
                        {
                            var nsMembers = nsSymbol.GetNamespaceMembers();
                            namespaceMembers.AddRange(nsMembers.Select(m => m.Name));
                            Assembly = nsSymbol.ContainingAssembly?.ToDisplayString();
                            Module = nsSymbol.ContainingModule?.ToDisplayString();
                            namespaceType = nsSymbol.ContainingNamespace?.ToDisplayString();
                        }
                    }

                    allUsingDirectives.Add(new Dependency
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        Assembly = Assembly,
                        Module = Module,
                        Namespace = namespaceType,
                        Name = name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        NamespaceMembers = namespaceMembers
                    });
                }
            }

            // method calls
            if (csMethodCalls != null)
            {
                foreach(var methodCall in csMethodCalls)
                {
                    if (model != null)
                        TrackCsMethodCall(methodCall, model, allMethodCalls, path, sourceFilePath, fileName);
                }
            }

            if (vbMethodCalls != null)
            {
                foreach(var methodCall in vbMethodCalls)
                {
                    if (model != null)
                        TrackVBMethodCall(methodCall, model, allMethodCalls, path, sourceFilePath, fileName);
                }
            }
        }

        // Process F# files
        var (fsharpMethods, fsharpDependencies, fsharpMethodCalls) = GetFSharpMethods(path);
        sourceMethods.AddRange(fsharpMethods);
        allUsingDirectives.AddRange(fsharpDependencies);
        allMethodCalls.AddRange(fsharpMethodCalls);
        
        string CreateNodeId(string ns, string className, string memberName) => $"{ns}.{className}.{memberName}";
        foreach (var method in sourceMethods)
        {
            var nodeId = CreateNodeId(method.Namespace ?? string.Empty, method.ClassName ?? string.Empty, method.Name ?? string.Empty);
            if (methodNodes.All(n => n.Id != nodeId))
            {
                methodNodes.Add(new MethodNode
                {
                    Id = nodeId,
                    Name = method.Name ?? string.Empty,
                    ClassName = method.ClassName ?? string.Empty,
                    Namespace = method.Namespace ?? string.Empty,
                    FileName = method.FileName ?? string.Empty
                });
            }
        }
        // Add nodes for Properties
        foreach (var prop in properties)
        {
            var nodeId = CreateNodeId(prop.Namespace ?? string.Empty, prop.ClassName ?? string.Empty, prop.Name ?? string.Empty);
            if (methodNodes.All(n => n.Id != nodeId))
            {
                methodNodes.Add(new MethodNode
                {
                    Id = nodeId,
                    Name = prop.Name ?? string.Empty,
                    ClassName = prop.ClassName ?? string.Empty,
                    Namespace = prop.Namespace ?? string.Empty,
                    FileName = prop.FileName ?? string.Empty
                });
            }
        }
        // Add nodes for Fields
        foreach (var field in fields)
        {
            var nodeId = CreateNodeId(field.Namespace ?? string.Empty, field.ClassName ?? string.Empty, field.Name ?? string.Empty);
            if (methodNodes.All(n => n.Id != nodeId))
            {
                methodNodes.Add(new MethodNode
                {
                    Id = nodeId,
                    Name = field.Name ?? string.Empty,
                    ClassName = field.ClassName ?? string.Empty,
                    Namespace = field.Namespace ?? string.Empty,
                    FileName = field.FileName ?? string.Empty
                });
            }
        }
        // Add nodes for Events
        foreach (var evt in events)
        {
            var nodeId = CreateNodeId(evt.Namespace ?? string.Empty, evt.ClassName ?? string.Empty, evt.Name ?? string.Empty);
            if (methodNodes.All(n => n.Id != nodeId))
            {
                methodNodes.Add(new MethodNode
                {
                    Id = nodeId,
                    Name = evt.Name ?? string.Empty,
                    ClassName = evt.ClassName ?? string.Empty,
                    Namespace = evt.Namespace ?? string.Empty,
                    FileName = evt.FileName ?? string.Empty
                });
            }
        }
        // Add nodes for Constructors
        foreach (var ctor in constructors)
        {
            var nodeId = CreateNodeId(ctor.Namespace ?? string.Empty, ctor.ClassName ?? string.Empty, ctor.Name ?? string.Empty);
            if (methodNodes.All(n => n.Id != nodeId))
            {
                methodNodes.Add(new MethodNode
                {
                    Id = nodeId,
                    Name = ctor.Name ?? string.Empty,
                    ClassName = ctor.ClassName ?? string.Empty,
                    Namespace = ctor.Namespace ?? string.Empty,
                    FileName = ctor.FileName ?? string.Empty
                });
            }
        }
        // 2. Create Edges based on tracked method calls
        foreach (var call in allMethodCalls)
        {
            // Determine the source node (the method/property/etc that made the call)
            if (!string.IsNullOrEmpty(call.CallerNamespace) &&
                !string.IsNullOrEmpty(call.CallerClass) &&
                !string.IsNullOrEmpty(call.CallerMethod))
            {
                var sourceNodeId = CreateNodeId(call.CallerNamespace, call.CallerClass, call.CallerMethod);
                if (!string.IsNullOrEmpty(call.Namespace) &&
                    !string.IsNullOrEmpty(call.ClassName) &&
                    !string.IsNullOrEmpty(call.CalledMethod))
                {
                    string targetMemberName;
                    var lastDotIndex = call.CalledMethod.LastIndexOf('.');
                    if (lastDotIndex >= 0 && lastDotIndex < call.CalledMethod.Length - 1)
                    {
                        targetMemberName = call.CalledMethod.Substring(lastDotIndex + 1);
                    }
                    else
                    {
                        targetMemberName = call.CalledMethod;
                    }
                    var targetNodeId = CreateNodeId(call.Namespace, call.ClassName, targetMemberName);
                    if (methodNodes.Any(n => n.Id == sourceNodeId))
                    {
                        callEdges.Add(new MethodCallEdge
                        {
                            SourceId = sourceNodeId,
                            TargetId = targetNodeId,
                            CallLocation = new CallLocation
                            {
                                FileName = call.FileName,
                                LineNumber = call.LineNumber,
                                ColumnNumber = call.ColumnNumber
                            },
                            CalledMethodName = call.CalledMethod, 
                            Arguments = call.Arguments ?? new List<string>(),
                            ArgumentExpressions = call.ArgumentExpressions ?? new List<string>(),
                            CallType = call.CallType
                        });
                    }
                }
            }
        }
        var callGraph = new CallGraph
        {
            Edges = callEdges,
            Nodes = methodNodes
        };
        // Source-to-Assembly Mapping Logic
        var assemblyMemberLookup = new Dictionary<string, object>();
        foreach (var asmMethod in assemblyMethods)
        {
            var asmSignature = asmMethod.AssemblySignature;
            if (asmSignature != null)
            {
                assemblyMemberLookup.TryAdd(asmSignature, asmMethod);
            }
        }

        foreach (var method in sourceMethods)
        {
            AddMapping(method, "Method");
        }
        return (sourceMethods, allUsingDirectives, allMethodCalls, properties, fields, events, constructors, callGraph, sourceAssemblyMappings);

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
        while (parent != null)
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
            if (argType != null)
            {
                callArgsTypes.Add(argType.ToDisplayString());
            }
            else
            {
                // Fallback to expression type if we can't get the type info
                callArgsTypes.Add(arg.Expression.GetType().Name);
            }
            callArgExpressions.Add(arg.Expression.ToFullString());
        }
        var exprInfo = model.GetSymbolInfo(callExpression);
        var calledMethod = string.Empty;
        var isInMetadata = false;
        var isInSource = false;
        var Assembly = string.Empty;
        var Module = string.Empty;
        var Namespace = string.Empty;
        var ClassName = string.Empty;
        var callerMethod = string.Empty;
        var callerNamespace = string.Empty;
        var callerClass = string.Empty;
        var callType = CallType.MethodCall;
        
        if (exprInfo.Symbol != null)
        {
            switch (exprInfo.Symbol)
            {
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.PropertyGet:
                    callType = CallType.PropertyGet;
                    calledMethod = methodSymbol.AssociatedSymbol?.Name ?? methodSymbol.Name;
                    ClassName = methodSymbol.AssociatedSymbol?.ContainingType?.Name ?? methodSymbol.ContainingType?.Name ?? "";
                    Namespace = methodSymbol.AssociatedSymbol?.ContainingNamespace?.ToDisplayString() ??
                                methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.PropertySet:
                    callType = CallType.PropertySet;
                    calledMethod = methodSymbol.AssociatedSymbol?.Name ?? methodSymbol.Name;
                    ClassName = methodSymbol.AssociatedSymbol?.ContainingType?.Name ?? methodSymbol.ContainingType?.Name ?? "";
                    Namespace = methodSymbol.AssociatedSymbol?.ContainingNamespace?.ToDisplayString() ??
                                methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.EventAdd:
                    callType = CallType.EventSubscribe;
                    calledMethod = methodSymbol.AssociatedSymbol?.Name ?? methodSymbol.Name;
                    ClassName = methodSymbol.AssociatedSymbol?.ContainingType?.Name ?? methodSymbol.ContainingType?.Name ?? "";
                    Namespace = methodSymbol.AssociatedSymbol?.ContainingNamespace?.ToDisplayString() ??
                                methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.EventRemove:
                    callType = CallType.EventUnsubscribe;
                    calledMethod = methodSymbol.AssociatedSymbol?.Name ?? methodSymbol.Name;
                    ClassName = methodSymbol.AssociatedSymbol?.ContainingType?.Name ?? methodSymbol.ContainingType?.Name ?? "";
                    Namespace = methodSymbol.AssociatedSymbol?.ContainingNamespace?.ToDisplayString() ??
                                methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.Constructor:
                    callType = CallType.ConstructorCall;
                    calledMethod = methodSymbol.ContainingType?.Name ?? methodSymbol.Name; // Constructor name is usually .ctor
                    ClassName = methodSymbol.ContainingType?.Name ?? "";
                    Namespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
                case IMethodSymbol methodSymbol:
                    calledMethod = methodSymbol.ToDisplayString();
                    isInMetadata = methodSymbol.Locations.Any(l => l.IsInMetadata);
                    isInSource = methodSymbol.Locations.Any(l => l.IsInSource);
                    Assembly = methodSymbol.ContainingAssembly?.ToDisplayString() ?? "";
                    Module = methodSymbol.ContainingModule?.ToDisplayString() ?? "";
                    Namespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    ClassName = methodSymbol.ContainingType?.Name ?? "";
                    break;
                case IPropertySymbol propertySymbol:
                    callType = CallType.PropertyGet;
                    calledMethod = propertySymbol.Name;
                    ClassName = propertySymbol.ContainingType?.Name ?? "";
                    Namespace = propertySymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    break;
            }
        }

        // Get caller context
        var callerSymbol = model.GetEnclosingSymbol(methodCall.SpanStart) as IMethodSymbol;
        if (callerSymbol != null)
        {
            callerMethod = callerSymbol.Name;
            callerNamespace = callerSymbol.ContainingNamespace.ToDisplayString();
            callerClass = callerSymbol.ContainingType.Name;
        }

        var IsInternal = isInSource || !isInMetadata;
        if (Assembly == String.Empty && Module == String.Empty && Namespace == String.Empty && ClassName == String.Empty && calledMethod == String.Empty)
        {
            return;
        }
        allMethodCalls.Add(new MethodCalls
        {
            Path = Path.GetRelativePath(path, sourceFilePath),
            FileName = fileName,
            Assembly = Assembly,
            Module = Module,
            Namespace = Namespace,
            ClassName = ClassName,
            CalledMethod = calledMethod,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            Arguments = callArgsTypes,
            ArgumentExpressions = callArgExpressions,
            CallType = callType,
            CallerMethod = callerMethod,
            CallerNamespace = callerNamespace,
            CallerClass = callerClass,
            IsInternal = IsInternal
        });
    }

    private static void TrackVBMethodCall(Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax methodCall, SemanticModel model, List<MethodCalls> allMethodCalls, string path, string sourceFilePath, string fileName)
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
        var Assembly = string.Empty;
        var Module = string.Empty;
        var Namespace = string.Empty;
        var ClassName = string.Empty;
        var callerMethod = string.Empty;
        var callerNamespace = string.Empty;
        var callerClass = string.Empty;

        if (exprInfo.Symbol != null)
        {
            var methodSymbol = exprInfo.Symbol;
            calledMethod = methodSymbol.ToDisplayString();
            isInMetadata = methodSymbol.Locations.Any(loc => loc.IsInMetadata);
            isInSource = methodSymbol.Locations.Any(loc => loc.IsInSource);
            Assembly = methodSymbol.ContainingAssembly.ToDisplayString();
            Module = methodSymbol.ContainingModule.ToDisplayString();
            Namespace = methodSymbol.ContainingNamespace.ToDisplayString();
            ClassName = methodSymbol.ContainingType.ToDisplayString();
        }

        // Get caller context
        var callerSymbol = model.GetEnclosingSymbol(methodCall.SpanStart) as IMethodSymbol;
        if (callerSymbol != null)
        {
            callerMethod = callerSymbol.Name;
            callerNamespace = callerSymbol.ContainingNamespace.ToDisplayString();
            callerClass = callerSymbol.ContainingType.Name;
        }

        var IsInternal = isInSource || !isInMetadata;

        allMethodCalls.Add(new MethodCalls
        {
            Path = Path.GetRelativePath(path, sourceFilePath),
            FileName = fileName,
            Assembly = Assembly,
            Module = Module,
            Namespace = Namespace,
            ClassName = ClassName,
            CalledMethod = calledMethod,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            Arguments = callArgsTypes,
            CallerMethod = callerMethod,
            CallerNamespace = callerNamespace,
            CallerClass = callerClass,
            IsInternal = IsInternal
        });
    }

    /// <summary>
    /// Get list of files to inspect for the given path and file extension
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <param name="fileExtension">File extension</param>
    /// <returns>List of files</returns>
    private static List<string> GetFilesToInspect(string path, string fileExtension)
    {
        var filesToInspect = new List<string>();
        var fileAttributes = File.GetAttributes(path);

        if (fileAttributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var inputFile in new DirectoryInfo(path).EnumerateFiles($"*{fileExtension}", SearchOption.AllDirectories))
            {
                // ignore generated cs/vb files
                if (!inputFile.FullName.EndsWith($".g{fileExtension}"))
                {
                    filesToInspect.Add(inputFile.FullName);
                }
            }
        }
        else
        {
            var extension = Path.GetExtension(path);

            if (!extension.Equals(Constants.AssemblyExtension) && !extension.Equals(Constants.CSharpSourceExtension) && !extension.Equals(Constants.VBSourceExtension) && !extension.Equals(Constants.FSharpSourceExtension))
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