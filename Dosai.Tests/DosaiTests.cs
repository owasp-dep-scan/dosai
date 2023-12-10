using Depscan;
using System.Text.Json;
using Xunit;

namespace Dosai.Tests;

public class DosaiTests
{
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
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        Assert.Equal(expectedMethodsDosaiTestsDLL.Length, actualMethods?.Count);
        // AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestsSource);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        Assert.Equal(expectedMethodsDosaiTestsSource.Length, actualMethods?.Count);
        // AssertMethods(actualMethods, expectedMethodsDosaiTestsSource);
    }

    [Fact]
    public void GetMethods_Assembly_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(assembliesDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(assembliesDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(assembliesDirectory, DosaiDLL), true);

        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), assembliesDirectory);
        var result = Depscan.Dosai.GetMethods(assemblyFolder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        Assert.Equal(100, actualMethods?.Count);
        // AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
        // AssertMethods(actualMethods, expectedMethodsDosaiDLL);
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
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        Assert.Equal(expectedMethodsDosaiTestsSource.Length + expectedMethodsAssemblySource.Length, actualMethods?.Count);
        // AssertMethods(actualMethods, expectedMethodsDosaiTestsSource);
        // AssertMethods(actualMethods, expectedMethodsAssemblySource);
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
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        Assert.Equal(121, actualMethods?.Count);
        // AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
        // AssertMethods(actualMethods, expectedMethodsDosaiDLL);
        // AssertMethods(actualMethods, expectedMethodsDosaiTestsSource);
        // AssertMethods(actualMethods, expectedMethodsAssemblySource);
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
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        Assert.Equal(0, methodsSlice?.Methods?.Count);
    }

    private static void AssertNamespaces(List<Namespace>? actualNamespaces, Namespace[] expectedNamespaces)
    {
        foreach(var expectedNamespace in expectedNamespaces)
        {
            var troubleshootingMessage = string.Empty;

            try
            {
                troubleshootingMessage = $"{expectedNamespace.FileName}.{expectedNamespace.Name}";
                var matchingNamespace = actualNamespaces?.Single(ns => ns.FileName == expectedNamespace.FileName &&
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
                troubleshootingMessage = $"{expectedMethod.FileName}.{expectedMethod.Namespace}.{expectedMethod.ClassName}.{expectedMethod.Attributes}.{expectedMethod.Name}.{expectedMethod.ReturnType}.{expectedMethod.LineNumber}.{expectedMethod.ColumnNumber}";
                matchingMethod = actualMethods?.Single(method => method.FileName == expectedMethod.FileName &&
                                                  method.Namespace == expectedMethod.Namespace &&
                                                  method.ClassName == expectedMethod.ClassName &&
                                                  method.Attributes == expectedMethod.Attributes &&
                                                  method.Name == expectedMethod.Name &&
                                                  method.ReturnType == expectedMethod.ReturnType &&
                                                  method.LineNumber == expectedMethod.LineNumber &&
                                                  method.ColumnNumber == expectedMethod.ColumnNumber);

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
            FileName = DosaiTestsDLL,
            Name = DosaiTestsNamespace
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = FooBarNamespace
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = HelloWorldNamespace
        }
    ];

    // Expected namespaces in Assembly.cs
    private static readonly Namespace[] expectedNamespacesAssemblySource =
    [
        new()
        {
            FileName = AssemblySource,
            Name = FooBarNamespace
        },
        new()
        {
            FileName = AssemblySource,
            Name = HelloWorldNamespace
        }
    ];

    // Expected namespaces in Dosai.dll
    private static readonly Namespace[] expectedNamespacesDosaiDLL =
    [
        new()
        {
            FileName = DosaiDLL,
            Name = DepscanNamespace
        }
    ];

    // Expected namespaces in DosaiTests.cs
    private static readonly Namespace[] expectedNamespacesDosaiTestsSource =
    [
        new()
        {
            FileName = DosaiTestsSource,
            Name = DosaiTestsNamespace
        }
    ];

    // Expected methods in Dosai.Tests.dll
    private static readonly Method[] expectedMethodsDosaiTestsDLL =
    [
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetNamespaces_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public, HideBySig",
            Name = "GetMethods_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = FooBarNamespace,
            ClassName = "Foo",
            Attributes = "Public, HideBySig",
            Name = "foo",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = FooBarNamespace,
            ClassName = "Bar",
            Attributes = "Public, HideBySig",
            Name = "bar",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = HelloWorldNamespace,
            ClassName = "Hello",
            Attributes = "Public, Static, HideBySig",
            Name = "elevate",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = HelloWorldNamespace,
            ClassName = "Hello",
            Attributes = "Public, HideBySig",
            Name = "Appreciate",
            ReturnType = Task,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = HelloWorldNamespace,
            ClassName = "World",
            Attributes = "Public, HideBySig",
            Name = "shout",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        }
    ];

    // Expected methods in DosaiTests.cs
    private static readonly Method[] expectedMethodsDosaiTestsSource =
    [
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 9,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 19,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 29,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 45,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 61,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            LineNumber = 84,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            LineNumber = 91,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetNamespaces_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            LineNumber = 98,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_Assembly_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 108,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_CSharpSource_PathIsFile_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 118,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_Assembly_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 128,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 144,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails",
            ReturnType = Void,
            LineNumber = 160,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_PathIsNotDLLFile_ThrowsException",
            ReturnType = Void,
            LineNumber = 183,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_PathDoesNotExist_ThrowsException",
            ReturnType = Void,
            LineNumber = 190,
            ColumnNumber = 4,
            Parameters = []
        },
        new()
        {
            FileName = DosaiTestsSource,
            Namespace = DosaiTestsNamespace,
            ClassName = "DosaiTests",
            Attributes = "Public",
            Name = "GetMethods_PathIsEmptyDirectory_ReturnsNothing",
            ReturnType = Void,
            LineNumber = 197,
            ColumnNumber = 4,
            Parameters = []
        }
    ];

    // Expected methods in Dosai.dll
    private static readonly Method[] expectedMethodsDosaiDLL =
    [
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Dosai",
            Attributes = "Public, Static, HideBySig",
            Name = "GetNamespaces",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
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
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Dosai",
            Attributes = "Public, Static, HideBySig",
            Name = "GetMethods",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
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
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_FileName",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_FileName",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Namespace",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Namespace",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Class",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Class",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Attributes",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Attributes",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Name",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Name",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_ReturnType",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_ReturnType",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_LineNumber",
            ReturnType = Int32,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_LineNumber",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = Int32
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_ColumnNumber",
            ReturnType = Int32,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_ColumnNumber",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = Int32
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Parameters",
            ReturnType = ListPrimeOne,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Method",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Parameters",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = ListPrimeOne
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_FileName",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_FileName",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Name",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Namespace",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Name",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Name",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Name",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "get_Type",
            ReturnType = String,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "Parameter",
            Attributes = "Public, HideBySig, SpecialName",
            Name = "set_Type",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters =
            [
                new()
                {
                    Name = value,
                    Type = String
                }
            ]
        },
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            ClassName = "CommandLine",
            Attributes = "Public, Static, HideBySig",
            Name = "Main",
            ReturnType = "Task`1",
            LineNumber = default,
            ColumnNumber = default,
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
            FileName = AssemblySource,
            Namespace = HelloWorldNamespace,
            ClassName = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = Void,
            LineNumber = 6,
            ColumnNumber = 8,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = HelloWorldNamespace,
            ClassName = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 11,
            ColumnNumber = 8,
            ReturnType = Task,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = HelloWorldNamespace,
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = Void,
            LineNumber = 19,
            ColumnNumber = 8,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = FooBarNamespace,
            ClassName = "Foo",
            Attributes = "Public",
            Name = "foo",
            ReturnType = Void,
            LineNumber = 35,
            ColumnNumber = 8,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = FooBarNamespace,
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = Void,
            LineNumber = 43,
            ColumnNumber = 8,
            Parameters = []
        }
    ];

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
    private const string ListPrimeOne = "List`1";
    private const string Task = "Task";
    private const string Int32 = "Int32";
    private const string value = "value";
    private const string assembliesDirectory = "assemblies";
    private const string sourceDirectory = "source";
    private const string emptyDirectory = "empty";
    private const string combinedDirectory = "combined";
}