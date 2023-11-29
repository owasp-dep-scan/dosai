using Depscan;
using System.Text.Json;
using Xunit;

namespace Dosai.Tests;

public class DosaiTests
{
    private const string DosaiTestsDLL = "Dosai.Tests.dll";
    private const string DosaiTestsPdb = "Dosai.Tests.pdb";
    private const string DosaiTestsSource = "DosaiTests.cs";
    private const string AssemblySource = "Assembly.cs";
    private const string DosaiDLL = "Dosai.dll";
    private const string FakeDLL = "Fake.dll";
    private const string DepscanNamespace = "Depscan";
    private const string DosaiTestsNamespace = "Dosai.Tests";
    private const string FooBarNamespace = "FooBar";
    private const string HelloWorldNamespace = "HelloWorld";
    private const string Void = "Void";
    private const string String = "String";
    private const string StringArray = "String[]";
    private const string List = "List";
    private const string Task = "Task";
    private const string assembliesDirectory = "assemblies";
    private const string sourceDirectory = "source";
    private const string emptyDirectory = "empty";
    private const string combinedDirectory = "combined";

    [Fact]
    public void GetNamespaces_Assembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestsDLL);
        var result = Depscan.Dosai.GetNamespaces(assemblyPath);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesDosaiTestsDLL.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestsDLL);
    }

    [Fact]
    public void GetNamespaces_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(DosaiTestsSource);
        var result = Depscan.Dosai.GetNamespaces(sourcePath);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesDosaiTestsSource.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestsSource);
    }

    [Fact]
    public void GetNamespaces_Assembly_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(assembliesDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(assembliesDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(assembliesDirectory, DosaiDLL), true);

        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), assembliesDirectory);
        var result = Depscan.Dosai.GetNamespaces(assemblyFolder);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesDosaiTestsDLL.Length + expectedNamespacesDosaiDLL.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestsDLL);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiDLL);
        Directory.Delete(assembliesDirectory, true);
    }

    [Fact]
    public void GetNamespaces_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(sourceDirectory);
        File.Copy(DosaiTestsSource, Path.Combine(sourceDirectory, DosaiTestsSource), true);
        File.Copy(AssemblySource, Path.Combine(sourceDirectory, AssemblySource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetNamespaces(sourceFolder);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesAssemblySource.Length + expectedNamespacesDosaiTestsSource.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesAssemblySource);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestsSource);
        Directory.Delete(sourceDirectory, true);
    }

    [Fact]
    public void GetNamespaces_AssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(combinedDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(combinedDirectory, DosaiDLL), true);
        File.Copy(DosaiTestsSource, Path.Combine(combinedDirectory, DosaiTestsSource), true);
        File.Copy(AssemblySource, Path.Combine(combinedDirectory, AssemblySource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetNamespaces(folder);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesDosaiTestsDLL.Length + 
                     expectedNamespacesDosaiDLL.Length + 
                     expectedNamespacesAssemblySource.Length +
                     expectedNamespacesDosaiTestsSource.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestsDLL);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiDLL);
        AssertNamespaces(actualNamespaces, expectedNamespacesAssemblySource);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestsSource);
        Directory.Delete(combinedDirectory, true);
    }

    [Fact]
    public void GetNamespaces_PathIsNotDLLFile_ThrowsException()
    {
        var assemblyPath = GetFilePath(DosaiTestsPdb);
        Assert.Throws<Exception>(() => Depscan.Dosai.GetNamespaces(assemblyPath));
    }

    [Fact]
    public void GetNamespaces_PathDoesNotExist_ThrowsException()
    {
        var assemblyPath = GetFilePath(FakeDLL);
        Assert.Throws<FileNotFoundException>(() => Depscan.Dosai.GetNamespaces(assemblyPath));
    }

    [Fact]
    public void GetNamespaces_PathIsEmptyDirectory_ReturnsNothing()
    {
        Directory.CreateDirectory(emptyDirectory);
        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), emptyDirectory);
        var result = Depscan.Dosai.GetNamespaces(assemblyFolder);
        var namespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(0, namespaces?.Count);
    }

    [Fact]
    public void GetMethods_Assembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestsDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);
        Assert.Equal(expectedMethodsDosaiTestsDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestsSource);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);
        Assert.Equal(expectedMethodsDosaiTestsSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsSource);
    }

    [Fact]
    public void GetMethods_Assembly_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(assembliesDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(assembliesDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(assembliesDirectory, DosaiDLL), true);

        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), assembliesDirectory);
        var result = Depscan.Dosai.GetMethods(assemblyFolder);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);
        Assert.Equal(expectedMethodsDosaiTestsDLL.Length + expectedMethodsDosaiDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
        AssertMethods(actualMethods, expectedMethodsDosaiDLL);
        Directory.Delete(assembliesDirectory, true);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(sourceDirectory);
        File.Copy(DosaiTestsSource, Path.Combine(sourceDirectory, DosaiTestsSource), true);
        File.Copy(AssemblySource, Path.Combine(sourceDirectory, AssemblySource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);
        Assert.Equal(expectedMethodsDosaiTestsSource.Length + expectedMethodsAssemblySource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsSource);
        AssertMethods(actualMethods, expectedMethodsAssemblySource);
        Directory.Delete(sourceDirectory, true);
    }

    [Fact]
    public void GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(combinedDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(combinedDirectory, DosaiDLL), true);
        File.Copy(DosaiTestsSource, Path.Combine(combinedDirectory, DosaiTestsSource), true);
        File.Copy(AssemblySource, Path.Combine(combinedDirectory, AssemblySource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);
        Assert.Equal(expectedMethodsDosaiTestsDLL.Length + 
                     expectedMethodsDosaiDLL.Length + 
                     expectedMethodsAssemblySource.Length +
                     expectedMethodsDosaiTestsSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
        AssertMethods(actualMethods, expectedMethodsDosaiDLL);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsSource);
        AssertMethods(actualMethods, expectedMethodsAssemblySource);
        Directory.Delete(combinedDirectory, true);
    }

    [Fact]
    public void GetMethods_PathIsNotDLLFile_ThrowsException()
    {
        var assemblyPath = GetFilePath(DosaiTestsPdb);
        Assert.Throws<Exception>(() => Depscan.Dosai.GetMethods(assemblyPath));
    }

    [Fact]
    public void GetMethods_PathDoesNotExist_ThrowsException()
    {
        var assemblyPath = GetFilePath(FakeDLL);
        Assert.Throws<FileNotFoundException>(() => Depscan.Dosai.GetMethods(assemblyPath));
    }

    [Fact]
    public void GetMethods_PathIsEmptyDirectory_ReturnsNothing()
    {
        Directory.CreateDirectory(emptyDirectory);
        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), emptyDirectory);
        var result = Depscan.Dosai.GetMethods(assemblyFolder);
        var methods = JsonSerializer.Deserialize<List<Method>>(result);
        Assert.Equal(0, methods?.Count);
    }

    private static void AssertNamespaces(List<Namespace>? actualNamespaces, Namespace[] expectedNamespaces)
    {
        foreach(var expectedNamespace in expectedNamespaces)
        {
            var troubleshootingMessage = string.Empty;

            try
            {
                troubleshootingMessage = $"{expectedNamespace.Module}.{expectedNamespace.Name}";
                var matchingNamespace = actualNamespaces?.Single(ns => ns.Module == expectedNamespace.Module && 
                                                                       ns.Name == expectedNamespace.Name);

                Assert.NotNull(matchingNamespace);
            }
            catch(Exception)
            {
                Assert.Fail($"Matching namespace not found. Expecting: {troubleshootingMessage}");
            }
        }
    }

    private static void AssertMethods(List<Method>? actualMethods, Method[] expectedMethods)
    {
        foreach(var expectedMethod in expectedMethods)
        {
            Method? matchingMethod = null;
            var troubleshootingMessage = string.Empty;

            try
            {
                troubleshootingMessage = $"{expectedMethod.Module}.{expectedMethod.Namespace}.{expectedMethod.Class}.{expectedMethod.Attributes}.{expectedMethod.Name}.{expectedMethod.ReturnType}";
                matchingMethod = actualMethods?.Single(method => method.Module == expectedMethod.Module &&
                                                  method.Namespace == expectedMethod.Namespace &&
                                                  method.Class == expectedMethod.Class &&
                                                  method.Attributes == expectedMethod.Attributes &&
                                                  method.Name == expectedMethod.Name &&
                                                  method.ReturnType == expectedMethod.ReturnType);

                Assert.NotNull(matchingMethod);
            }
            catch(Exception)
            {
                Assert.Fail($"Matching method not found. Expecting: {troubleshootingMessage}");
            }
            
            if (expectedMethod.Parameters != null)
            {
                foreach (var expectedParameter in expectedMethod.Parameters)
                {
                    Assert.True(matchingMethod?.Parameters?.Exists(parameter => parameter.Name == expectedParameter.Name && 
                                                                                parameter.Type == expectedParameter.Type));
                }
            }
            
        }
    }

    private static string GetFilePath(string filePath)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        return Path.Join(currentDirectory, filePath);
    }

    // Expected namespaces in Dosai.Tests.dll
    private static readonly Namespace[] expectedNamespacesDosaiTestsDLL =
    [
        new()
        {
            Module = DosaiTestsDLL,
            Name = DosaiTestsNamespace
        },
        new()
        {
            Module = DosaiTestsDLL,
            Name = FooBarNamespace
        },
        new()
        {
            Module = DosaiTestsDLL,
            Name = HelloWorldNamespace
        }
    ];

    // Expected namespaces in Assembly.cs
    private static readonly Namespace[] expectedNamespacesAssemblySource =
    [
        new()
        {
            Module = AssemblySource,
            Name = FooBarNamespace
        },
        new()
        {
            Module = AssemblySource,
            Name = HelloWorldNamespace
        }
    ];

    // Expected namespaces in Dosai.dll
    private static readonly Namespace[] expectedNamespacesDosaiDLL =
    [
        new()
        {
            Module = DosaiDLL,
            Name = DepscanNamespace
        }
    ];

    // Expected namespaces in DosaiTests.cs
    private static readonly Namespace[] expectedNamespacesDosaiTestsSource =
    [
        new()
        {
            Module = DosaiTestsSource,
            Name = DosaiTestsNamespace
        }
    ];

    // Expected methods in Dosai.Tests.dll
    private static readonly Method[] expectedMethodsDosaiTestsDLL =
    [
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = FooBarNamespace,
            Class = "Foo",
            Attributes = "Public, HideBySig",
            Name = "foo",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = FooBarNamespace,
            Class = "Bar",
            Attributes = "Public, HideBySig",
            Name = "bar",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = HelloWorldNamespace,
            Class = "Hello",
            Attributes = "Public, Static, HideBySig",
            Name = "elevate",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = HelloWorldNamespace,
            Class = "Hello",
            Attributes = "Public, HideBySig",
            Name = "Appreciate",
            ReturnType = Task,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsDLL,
            Namespace = HelloWorldNamespace,
            Class = "World",
            Attributes = "Public, HideBySig",
            Name = "shout",
            ReturnType = Void,
            Parameters = []
        }
    ];

    // Expected methods in DosaiTests.cs
    private static readonly Method[] expectedMethodsDosaiTestsSource =
    [
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            Class = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            Parameters = []
        }
    ];

    // Expected methods in Dosai.dll
    private static readonly Method[] expectedMethodsDosaiDLL =
    [
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Dosai",
            Attributes = "Public, Static, HideBySig",
            Name = "GetNamespaces",
            ReturnType = String,
            Parameters =
            [
                new()
                {
                    Name = "path",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Dosai",
            Attributes = "Public, Static, HideBySig",
            Name = "GetMethods",
            ReturnType = String,
            Parameters =
            [
                new()
                {
                    Name = "path",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Module",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Module",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Namespace",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Namespace",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Class",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Class",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Attributes",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Attributes",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Name",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Name",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_ReturnType",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_ReturnType",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Parameters",
            ReturnType = List,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Parameters",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = List
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Module",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Module",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Name",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Name",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Name",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Name",
            ReturnType = Void,
            Parameters =
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Type",
            ReturnType = String,
            Parameters = []
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Type",
            ReturnType = Void,
            Parameters = 
            [
                new()
                {
                    Name = "value",
                    Type = String
                }
            ]
        },
        new()
        {
            Module = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "CommandLine",
            Attributes = "Public, Static, HideBySig",
            Name = "Main",
            ReturnType = Task,
            Parameters = 
            [
                new()
                {
                    Name = "args",
                    Type = StringArray
                }
            ]
        }
    ];

    // Expected methods in Assembly.cs
    private static readonly Method[] expectedMethodsAssemblySource =
    [
        new()
        {
            Module = AssemblySource,
            Namespace = FooBarNamespace,
            Class = "Foo",
            Attributes = "Public",
            Name = "foo",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = AssemblySource,
            Namespace = FooBarNamespace,
            Class = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = AssemblySource,
            Namespace = HelloWorldNamespace,
            Class = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = Void,
            Parameters = []
        },
        new()
        {
            Module = AssemblySource,
            Namespace = HelloWorldNamespace,
            Class = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            ReturnType = Task,
            Parameters = []
        },
        new()
        {
            Module = AssemblySource,
            Namespace = HelloWorldNamespace,
            Class = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = Void,
            Parameters = []
        }
    ];
}