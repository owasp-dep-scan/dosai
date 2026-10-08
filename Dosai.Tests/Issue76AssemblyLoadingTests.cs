using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Dosai.Tests;

// Issue #76: assembly inspection loads the files of each inspected directory through one
// shared InspectionAssemblyLoadContext instead of a fresh per-assembly context, so shared
// dependencies load once per directory rather than once per inspected assembly (on Windows
// every LoadFromStream pays an AmsiScanBuffer call). These tests pin the semantics that
// sharing must not change: the extracted member inventory (against a verbatim copy of the
// old per-assembly loop kept here as the reference), per-directory dependency resolution,
// partial inventories when dependencies are missing, the structured attribute encoding,
// and deletability of inspected files while a scan is still running.
public sealed class Issue76AssemblyLoadingTests
{
    private static readonly JsonSerializerOptions RenderOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void AssemblyInspection_SharedDirectoryContexts_MatchThePerAssemblyReferenceLoop()
    {
        using var fixture = new TempDir();
        var output = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(output);
        var shared = EmitAssembly(output, "Issue76Shared",
            """
            namespace Issue76Shared;

            public class Calculator
            {
                public int Add(int left, int right) => left + right;
            }
            """);
        EmitAssembly(output, "Issue76Lib",
            """
            using Issue76Shared;

            namespace Issue76Lib;

            public class Service
            {
                public string Combine(Calculator calc, int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            """, shared);
        EmitAssembly(output, "Issue76App",
            """
            using Issue76Lib;

            namespace Issue76App;

            public class Program
            {
                public static void Main()
                {
                    var service = new Service();
                    _ = service.Combine(null!, 1);
                }
            }
            """, shared, Path.Combine(output, "Issue76Lib.dll"));

        // A native file and a same-identity copy: the loop skips both, exactly as before.
        File.WriteAllBytes(Path.Combine(output, "native.bin"), [0x4D, 0x5A, 0x00, 0x01]);
        File.Copy(Path.Combine(output, "Issue76App.dll"), Path.Combine(output, "Issue76AppCopy.dll"));

        var slice = Depscan.Dosai.GetMethodsSlice(output);

        var reference = InspectWithPerAssemblyContexts(output);
        var expected = GroupByFile(reference);
        var actual = GroupByFile(slice.Methods!);

        Assert.Equal(reference.Count, slice.Methods!.Count);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var fileName in expected.Keys)
        {
            Assert.True(JsonSerializer.Serialize(expected[fileName], RenderOptions)
                == JsonSerializer.Serialize(actual[fileName], RenderOptions),
                $"member inventory of {fileName} differs from the per-assembly reference");
        }
    }

    [Fact]
    public void AssemblyInspection_TwoDirectoriesWithDifferentDependencyContent_ResolvePerDirectory()
    {
        using var fixture = new TempDir();
        var dirA = Path.Combine(fixture.Path, "A");
        var dirB = Path.Combine(fixture.Path, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        // The same assembly name in both directories, different content: whichever context
        // loads it must resolve to its own directory's copy, so a directory's dependencies
        // keep the per-assembly resolution order (own directory first).
        EmitAssembly(dirA, "Issue76Dep",
            """
            namespace Issue76V1;

            public class Shared
            {
                public string Version() => "one";
            }
            """);
        EmitAssembly(dirB, "Issue76Dep",
            """
            namespace Issue76V2;

            public class Shared
            {
                public int Count() => 2;
            }
            """);
        EmitAssembly(dirA, "Issue76AppA",
            """
            namespace Issue76AppA;

            public class UsesA
            {
                public string Pick(Issue76V1.Shared shared) => shared.Version();
            }
            """, Path.Combine(dirA, "Issue76Dep.dll"));
        EmitAssembly(dirB, "Issue76AppB",
            """
            namespace Issue76AppB;

            public class UsesB
            {
                public int Pick(Issue76V2.Shared shared) => shared.Count();
            }
            """, Path.Combine(dirB, "Issue76Dep.dll"));

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        var appA = slice.Methods!.Single(method => method.FileName == "Issue76AppA.dll"
            && method.Name == "Pick");
        var appB = slice.Methods!.Single(method => method.FileName == "Issue76AppB.dll"
            && method.Name == "Pick");
        Assert.Contains(appA.Parameters!, parameter => parameter.TypeFullName == "Issue76V1.Shared");
        Assert.Contains(appB.Parameters!, parameter => parameter.TypeFullName == "Issue76V2.Shared");
    }

    [Fact]
    public void AssemblyInspection_StateLeftInTheSharedContextByEarlierFiles_DoesNotChangeLaterFiles()
    {
        // The two cases where a shared context answers a later file from what an earlier file
        // left in it, instead of probing afresh as a per-assembly context did:
        // - version skew: the folder ships Lib 1.0, the app was compiled against Lib 2.0, and
        //   Lib is inspected first, so the app's 2.0 request meets an already-loaded 1.0;
        // - a cached type-load failure: Mid's base type is missing and Mid is inspected first,
        //   so the consumer's signatures over Mid's broken type hit the failure Mid left.
        // Both must render exactly what the per-assembly reference renders.
        using var fixture = new TempDir();
        var compiledAgainst = Path.Combine(fixture.Path, "v2");
        var withheld = Path.Combine(fixture.Path, "withheld");
        var output = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(compiledAgainst);
        Directory.CreateDirectory(withheld);
        Directory.CreateDirectory(output);
        const string LibSource = """
            [assembly: System.Reflection.AssemblyVersion("{0}")]
            namespace Issue76Skew;

            public class Widget
            {{
                public virtual string Name() => "{0}";
            }}
            """;
        var libV2 = EmitAssembly(compiledAgainst, "Issue76AaLib", string.Format(System.Globalization.CultureInfo.InvariantCulture, LibSource, "2.0.0.0"));
        EmitAssembly(output, "Issue76AaLib", string.Format(System.Globalization.CultureInfo.InvariantCulture, LibSource, "1.0.0.0"));
        EmitAssembly(output, "Issue76ApApp",
            """
            namespace Issue76SkewApp;

            public class Fancy : Issue76Skew.Widget
            {
                public override string Name() => "fancy";
                public Issue76Skew.Widget Wrap(Issue76Skew.Widget inner) => inner;
            }
            """, libV2);

        var baseLib = EmitAssembly(withheld, "Issue76MissingBase",
            """
            namespace Issue76MissingBase;

            public abstract class Root
            {
            }
            """);
        var mid = EmitAssembly(output, "Issue76BaMid",
            """
            namespace Issue76Mid;

            public class Broken : Issue76MissingBase.Root
            {
                public int Value() => 1;
            }

            public class Fine
            {
                public int Other() => 2;
            }
            """, baseLib);
        EmitAssembly(output, "Issue76BpConsumer",
            """
            namespace Issue76Consumer;

            public class UsesMid
            {
                public Issue76Mid.Fine KeepsFine(Issue76Mid.Fine fine) => fine;
                public object TouchesBroken(Issue76Mid.Broken broken) => broken;
            }
            """, mid, baseLib);
        Directory.Delete(withheld, recursive: true);

        var slice = Depscan.Dosai.GetMethodsSlice(output);
        var reference = InspectWithPerAssemblyContexts(output);
        // The fixture has to reach both shapes, or the comparison below proves nothing.
        Assert.Contains(slice.Methods!, method => method.FileName == "Issue76ApApp.dll" && method.Name == "Wrap");
        Assert.Contains(slice.Methods!, method => method.FileName == "Issue76BaMid.dll" && method.Name == "Other");
        Assert.DoesNotContain(slice.Methods!, method => method.FileName == "Issue76BaMid.dll" && method.Name == "Value");
        Assert.Equal(
            JsonSerializer.Serialize(GroupByFile(reference), RenderOptions),
            JsonSerializer.Serialize(GroupByFile(slice.Methods!), RenderOptions));
    }

    [Fact]
    public void AssemblyInspection_MissingBaseAndAttributeTypes_PartialInventoryWithContainment()
    {
        using var fixture = new TempDir();
        var withheld = Path.Combine(fixture.Path, "withheld");
        var output = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(withheld);
        Directory.CreateDirectory(output);
        var baseLib = EmitAssembly(withheld, "Issue76Base",
            """
            namespace Issue76Base;

            public abstract class BaseRoot
            {
                public abstract string Render();
            }
            """);
        var attributeLib = EmitAssembly(withheld, "Issue76Attributes",
            """
            namespace Issue76Attributes;

            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class MarkAttribute : System.Attribute
            {
                public MarkAttribute(string tag) { }
            }
            """);
        EmitAssembly(output, "Issue76Partial",
            """
            namespace Issue76Partial;

            [Issue76Attributes.Mark("type")]
            public class Decorated : Issue76Base.BaseRoot
            {
                [Issue76Attributes.Mark("property")]
                public int Computed { get; set; }

                public override string Render() => "decorated";
            }

            public class Intact
            {
                public string Plain() => "plain";
            }
            """, baseLib, attributeLib);
        // The two dependencies live outside the scan root and outside every probed
        // directory: the base type and the attribute type cannot be resolved.
        Directory.Delete(withheld, recursive: true);

        var slice = Depscan.Dosai.GetMethodsSlice(output);
        Assert.Contains(slice.Methods!, method => method.FileName == "Issue76Partial.dll"
            && method.ClassName == "Intact" && method.Name == "Plain");

        // The reference loop must produce the same partial inventory: sharing the context
        // across the directory cannot change what a missing dependency costs.
        var reference = InspectWithPerAssemblyContexts(output);
        Assert.Equal(
            JsonSerializer.Serialize(GroupByFile(reference), RenderOptions),
            JsonSerializer.Serialize(GroupByFile(slice.Methods!), RenderOptions));
    }

    [Fact]
    public void AssemblyInspection_AttributeArgumentShapes_RenderTheStructuredEncoding()
    {
        using var fixture = new TempDir();
        var output = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(output);
        EmitAssembly(output, "Issue76AttrShapes", """
            using System;

            namespace Issue76AttrShapes;

            public enum Color
            {
                Red = 1,
                Green = 2
            }

            [AttributeUsage(AttributeTargets.All)]
            public sealed class EverythingAttribute : Attribute
            {
                public EverythingAttribute(string text, int number, Color color, Type type) { }
                public EverythingAttribute(string only) { }
                public EverythingAttribute(string[] texts) { }
                public EverythingAttribute(string[] texts, Color[] colors, Type[] types) { }

                public string NamedText { get; set; } = "";
                public int NamedNumber { get; set; }
                public Color NamedColor { get; set; }
                public bool NamedFlag { get; set; }
                public string[] NamedTexts { get; set; } = [];
                public object NamedBoxed { get; set; } = "";
                public long NamedField;
                public string NeverSet { get; set; } = "";
            }

            public class Decorated
            {
                [Everything(new[] { "x", null }, new[] { Color.Red, (Color)99 }, new[] { typeof(int), typeof(Decorated[]) })]
                public int Property { get; set; }

                [Everything((string?)null)]
                public Decorated() { }

                [Everything((string[]?)null)]
                public long Field;

                [Everything("event")]
                public event EventHandler? Happened;

                [Everything("generic", 7, (Color)200, typeof(Decorated),
                    NamedText = "n", NamedNumber = -7, NamedColor = Color.Red, NamedFlag = true,
                    NamedTexts = new[] { "a", null, "b" }, NamedBoxed = "box", NamedField = 9_007_199_254_740_993)]
                public void Method<T>(T value) { }
            }
            """);

        var slice = Depscan.Dosai.GetMethodsSlice(output);

        var property = slice.Methods!.Single(method => method.FileName == "Issue76AttrShapes.dll"
            && method.Name == "Property");
        var everything = property.CustomAttributes!.Single(attribute => attribute.Name == "EverythingAttribute");
        Assert.Equal("Issue76AttrShapes.EverythingAttribute", everything.FullName);
        // Constructor arguments keep the structured encoding: arrays carry elements, a null
        // string is IsNull without touching the array branch, typeof renders the type name,
        // enums render their names (including unnamed values), numbers stay invariant.
        Assert.Collection(everything.ConstructorArguments!,
            argument =>
            {
                Assert.Equal("System.String[]", argument.Type);
                Assert.True(argument.IsArray);
                Assert.Collection(argument.Elements!,
                    element => { Assert.Equal("System.String", element.Type); Assert.Equal("x", element.Value); },
                    element => { Assert.True(element.IsNull); Assert.Equal("System.String", element.Type); });
            },
            argument =>
            {
                Assert.Equal("Issue76AttrShapes.Color[]", argument.Type);
                Assert.Collection(argument.Elements!,
                    element => { Assert.Equal("Issue76AttrShapes.Color", element.Type); Assert.Equal("1", element.Value); },
                    element => { Assert.Equal("Issue76AttrShapes.Color", element.Type); Assert.Equal("99", element.Value); });
            },
            argument =>
            {
                Assert.Equal("System.Type[]", argument.Type);
                Assert.Collection(argument.Elements!,
                    element => { Assert.Equal("System.Type", element.Type); Assert.Equal("System.Int32", element.Value); },
                    element => { Assert.Equal("System.Type", element.Type); Assert.Equal("Issue76AttrShapes.Decorated[]", element.Value); });
            });
        Assert.Empty(everything.NamedArguments!);

        var ctor = slice.Methods!.Single(method => method.FileName == "Issue76AttrShapes.dll"
            && method.ClassName == "Decorated" && method.Name == ".ctor");
        var nullArgument = ctor.CustomAttributes!.Single(attribute => attribute.Name == "EverythingAttribute").ConstructorArguments!.Single();
        Assert.True(nullArgument.IsNull);
        Assert.False(nullArgument.IsArray);
        Assert.Equal("System.String", nullArgument.Type);

        var field = slice.Methods!.Single(method => method.FileName == "Issue76AttrShapes.dll"
            && method.ClassName == "Decorated" && method.Name == "Field");
        var nullArray = field.CustomAttributes!.Single(attribute => attribute.Name == "EverythingAttribute").ConstructorArguments!.Single();
        Assert.True(nullArray.IsNull);
        Assert.True(nullArray.IsArray);
        Assert.Equal("System.String[]", nullArray.Type);

        var genericMethod = slice.Methods!.Single(method => method.FileName == "Issue76AttrShapes.dll"
            && method.ClassName == "Decorated" && method.Name == "Method" && method.GenericParameters!.Count == 1);
        var methodAttribute = genericMethod.CustomAttributes!.Single(attribute => attribute.Name == "EverythingAttribute");
        Assert.Collection(methodAttribute.ConstructorArguments!,
            argument => { Assert.Equal("System.String", argument.Type); Assert.Equal("generic", argument.Value); },
            argument => { Assert.Equal("System.Int32", argument.Type); Assert.Equal("7", argument.Value); },
            argument => { Assert.Equal("Issue76AttrShapes.Color", argument.Type); Assert.Equal("200", argument.Value); },
            argument => { Assert.Equal("System.Type", argument.Type); Assert.Equal("Issue76AttrShapes.Decorated", argument.Value); });
        // Named arguments keep the scalar string shape, arrays flatten comma-joined, and an
        // unset member is simply absent.
        var named = methodAttribute.NamedArguments!.ToDictionary(argument => argument.Name ?? string.Empty, argument => argument.Value);
        Assert.Equal("n", named["NamedText"]);
        Assert.Equal("-7", named["NamedNumber"]);
        // A named enum argument renders its numeric value: CustomAttributeTypedArgument boxes
        // enum arguments as the underlying type, and the scalar named-argument shape goes
        // through Convert.ToString - "1", never "Red".
        Assert.Equal("1", named["NamedColor"]);
        Assert.Equal("True", named["NamedFlag"]);
        Assert.Equal("a,,b", named["NamedTexts"]);
        Assert.Equal("box", named["NamedBoxed"]);
        Assert.Equal("9007199254740993", named["NamedField"]);
        Assert.False(named.ContainsKey("NeverSet"));
    }

    [Fact]
    public void AssemblyInspection_InspectedFilesDeletedAfterTheScan_StayDeletable()
    {
        // The after-the-run half of #51's constraint, next to the phase-gated concurrent test
        // below: loading by value leaves no mapped handle, so the inspected directory is
        // deletable the moment the scan returns.
        using var fixture = new TempDir();
        var output = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(output);
        EmitAssembly(output, "Issue76After",
            """
            namespace Issue76After;

            public static class Program
            {
                public static void Main() { }
            }
            """);
        Assert.Contains(Depscan.Dosai.GetMethodsSlice(output).Methods!,
            method => method.FileName == "Issue76After.dll");
        Directory.Delete(output, recursive: true);
        Assert.False(Directory.Exists(output));
    }

    #region The pre-#76 per-assembly reference

    /// <summary>
    ///     The inspection loop before issue #76 (one collectible
    ///     <see cref="InspectionAssemblyLoadContext" /> per assembly, unloaded per file), kept
    ///     as the independent reference the shared per-directory contexts must reproduce. It
    ///     keeps the pipeline's exception handlers (a load failure part-way through an assembly
    ///     keeps the members already added) and drops only debug logging and console output.
    ///     Shared-framework probing is the running runtime's directory alone, not
    ///     <c>GetSharedFrameworkProbingPaths</c>: the fixtures reference nothing beyond the core
    ///     library, so the two agree there and nowhere else.
    /// </summary>
    private static List<Method> InspectWithPerAssemblyContexts(string path)
    {
        var assembliesToInspect = AssemblyScope.ScopeApplicationAssemblies(path,
            SafeFileRead.EnumerateAllFilesSafe(path).Where(file =>
                file.EndsWith(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(Constants.ExeExtension, StringComparison.OrdinalIgnoreCase)),
            _ => { });
        var assemblyMethods = new List<Method>();
        var processedAssemblyIdentities = new HashSet<string>();
        var sharedFrameworkDirs = new List<string> { System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };
        foreach (var assemblyFilePath in assembliesToInspect)
        {
            if (!IsManagedAssembly(assemblyFilePath))
            {
                continue;
            }
            var fileName = Path.GetFileName(assemblyFilePath);
            var inspectedDirs = new List<string> { Path.GetDirectoryName(assemblyFilePath)!, Path.GetDirectoryName(path)! };
            var loadContext = new InspectionAssemblyLoadContext(inspectedDirs, sharedFrameworkDirs);
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
            catch (Exception e) when (e is FileLoadException or FileNotFoundException or BadImageFormatException or TypeLoadException or NotSupportedException)
            {
                // The pipeline's handler: the members this assembly already added stay, the
                // rest of it is skipped. A reference without it cannot reach this path at all.
            }
            catch (Exception)
            {
            }
            finally
            {
                loadContext.Unload();
            }
        }

        return assemblyMethods;
    }

    private static Method CreateMethodObjectFromMember(
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
            CustomAttributes = Depscan.Dosai.ExtractCustomAttributes(member, []),
            BaseType = baseType,
            ImplementedInterfaces = implementedInterfaces,
            MetadataToken = metadataToken,
            AssemblySignature = assemblySignature,
            IsGenericMethod = isGeneric,
            IsGenericMethodDefinition = isGenericDef,
            GenericParameters = genericParams ?? []
        };
    }

    private static bool IsManagedAssembly(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(fs);
            return peReader is { HasMetadata: true, PEHeaders.CorHeader: not null };
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Fixture plumbing

    internal static string EmitAssembly(string outputDirectory, string assemblyName, string source, params string[] referencePaths)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference)
            .Concat(referencePaths.Select(path => MetadataReference.CreateFromFile(path)))
            .ToList();
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: $"{assemblyName}.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        var emit = compilation.Emit(dllPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine,
            emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    // Only the fields the inspection loop renders: later phases attach Identity and Purl to
    // the same objects, which the raw reference loop does not produce.
    private static object ProjectInspectionFields(Method method) => new
    {
        method.Path,
        method.FileName,
        method.Module,
        method.Namespace,
        method.ClassName,
        method.Attributes,
        method.Name,
        method.ReturnType,
        method.Parameters,
        method.CustomAttributes,
        method.BaseType,
        method.ImplementedInterfaces,
        method.MetadataToken,
        method.AssemblySignature,
        method.IsGenericMethod,
        method.IsGenericMethodDefinition,
        method.GenericParameters
    };

    private static Dictionary<string, List<object>> GroupByFile(IEnumerable<Method> methods)
        => methods.GroupBy(method => method.FileName ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(method => method.AssemblySignature, StringComparer.Ordinal)
                    .Select(ProjectInspectionFields)
                    .ToList());

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue76-" + Guid.NewGuid().ToString("N"));

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

    #endregion
}

// Partial DosaiTests member: the concurrent-deletion test redirects the process-wide
// stderr and flips the static DebugLog flag to gate on phase lines, so it must run in the
// same xunit collection as the other console-redirecting tests (see DebugLoggingTests).
public partial class DosaiTests
{
    // Issue #76 acceptance: inspected files stay deletable and replaceable DURING the
    // inspection phase. Loading reads each assembly by value under FileShare.ReadWrite|Delete
    // and closes the stream as soon as the load returns, so a concurrent deleter can remove
    // and restore every inspected file while the loop is still working through the folder.
    // The deleter is gated on the debug phase lines so it only touches files while
    // inspection is running, and stops after a bounded window well before the phase can
    // end, so the later source phase (which opens the dlls through Roslyn) never sees a
    // deleted name. A delete landing in the microseconds a file is open legitimately skips
    // that assembly for this run, so the concurrent slice's inventory is not asserted;
    // what is asserted is that every deletion succeeded, the scan did not fault, and a
    // follow-up scan of the restored folder sees the full inventory.
    [Fact]
    public async Task AssemblyInspection_InspectedFilesReplacedDuringTheInspectionPhase_ScanCompletes()
    {
        using var fixture = new TemporaryDirectory();
        var output = Path.Combine(fixture.Path, "bin");
        var backup = Path.Combine(fixture.Path, "backup");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(backup);
        var big = new System.Text.StringBuilder().AppendLine("namespace Issue76Big;");
        for (var index = 0; index < 15_000; index++)
        {
            big.AppendLine($"public sealed class Row{index} {{ public int Value{index}() => {index}; }}");
        }
        Issue76AssemblyLoadingTests.EmitAssembly(output, "Issue76Big", big.ToString());
        Issue76AssemblyLoadingTests.EmitAssembly(output, "Issue76Side",
            """
            namespace Issue76Side;

            public static class Helper
            {
                public static string Name() => nameof(Helper);
            }
            """);
        foreach (var dll in Directory.GetFiles(output, "*.dll"))
        {
            File.Copy(dll, Path.Combine(backup, Path.GetFileName(dll)));
        }

        var watcher = new InspectionPhaseWatcher();
        MethodsSlice? slice = null;
        Exception? fault = null;
        var deletions = 0;
        var deleter = new Thread(() =>
        {
            while (!watcher.InspectionStarted && fault is null && slice is null)
            {
                Thread.Sleep(1);
            }
            // A short breath after the phase line: the managed-file probe that opens every
            // candidate once runs first inside the phase, and a file absent at its probe is
            // skipped for the whole run - which this test does not need to race with.
            Thread.Sleep(50);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (watcher.InspectionStarted && !watcher.InspectionEnded && fault is null
                   && slice is null && deadline.Elapsed < TimeSpan.FromMilliseconds(500))
            {
                foreach (var dll in Directory.GetFiles(output, "*.dll"))
                {
                    try
                    {
                        File.Delete(dll);
                        Interlocked.Increment(ref deletions);
                        File.Copy(Path.Combine(backup, Path.GetFileName(dll)), dll);
                    }
                    catch (IOException)
                    {
                        // Delete-pending name on Windows or a concurrent open: the next pass retries.
                    }
                }
            }
            // The restore guarantee: no file may stay deleted once the deleter is done.
            foreach (var dll in Directory.GetFiles(backup, "*.dll"))
            {
                var target = Path.Combine(output, Path.GetFileName(dll));
                if (!File.Exists(target))
                {
                    File.Copy(dll, target);
                }
            }
        })
        {
            IsBackground = true
        };

        lock (ConsoleOutputLock)
        {
            var originalError = Console.Error;
            Console.SetError(watcher);
            try
            {
                DebugLog.Configure(true);
                deleter.Start();
                try
                {
                    InspectionPhaseWatcher.OwnScan.Value = true;
                    slice = Depscan.Dosai.GetMethodsSlice(output);
                }
                catch (Exception e)
                {
                    fault = e;
                }
            }
            finally
            {
                InspectionPhaseWatcher.OwnScan.Value = false;
                DebugLog.Configure(false);
                Console.SetError(originalError);
            }
        }

        deleter.Join(TimeSpan.FromSeconds(10));
        Assert.Null(fault);
        Assert.NotNull(slice);
        Assert.True(Volatile.Read(ref deletions) > 0,
            "the deleter never deleted a file while the inspection phase was running");

        // The folder is whole again and a fresh scan sees the untouched inventory.
        var after = Depscan.Dosai.GetMethodsSlice(output);
        Assert.Contains(after.Methods!, method => method.FileName == "Issue76Big.dll"
            && method.ClassName!.StartsWith("Row", StringComparison.Ordinal));
        Assert.Contains(after.Methods!, method => method.FileName == "Issue76Side.dll"
            && method.Name == "Name");
    }

    /// <summary>
    ///     Flips flags when this test's inspection-phase debug lines pass through stderr. The
    ///     debug flag and stderr are process-wide, so a test in another collection that runs the
    ///     methods pipeline meanwhile (crypto builds its reachability from it) writes the same
    ///     phase lines; they would start or stop the deleter while this scan is in its source
    ///     phase. Only lines written from this scan's execution context (the analysis threads
    ///     inherit <see cref="OwnScan" /> when they start) count.
    /// </summary>
    private sealed class InspectionPhaseWatcher : TextWriter
    {
        public static readonly AsyncLocal<bool> OwnScan = new();
        public volatile bool InspectionStarted;
        public volatile bool InspectionEnded;

        public override Encoding Encoding { get; } = Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value is null || !OwnScan.Value)
            {
                return;
            }
            if (value.Contains("start methods.assembly-inspection", StringComparison.Ordinal))
            {
                InspectionStarted = true;
            }
            if (value.Contains("end methods.assembly-inspection", StringComparison.Ordinal))
            {
                InspectionEnded = true;
            }
        }
    }
}
