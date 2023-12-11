using Depscan;
using System.Text.Json;
using Xunit;

namespace Dosai.Tests;

public class DosaiTests
{
    #region Namespaces
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
    #endregion Namespaces

    #region Methods
    [Fact]
    public void GetMethods_Assembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestsDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);

        if(actualMethods != null)
        {
            actualMethods = FilterOutTestMethods(actualMethods);
        }
        
        Assert.Equal(expectedMethodsDosaiTestsDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(DosaiSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);

        Assert.Equal(expectedMethodsDosaiSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiSource);
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

        if(actualMethods != null)
        {
            actualMethods = FilterOutTestMethods(actualMethods);
        }

        Assert.Equal(expectedMethodsDosaiTestsDLL.Length + expectedMethodsDosaiDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
        AssertMethods(actualMethods, expectedMethodsDosaiDLL);
        Directory.Delete(assembliesDirectory, true);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(sourceDirectory);
        File.Copy(DosaiSource, Path.Combine(sourceDirectory, DosaiSource), true);
        File.Copy(AssemblySource, Path.Combine(sourceDirectory, AssemblySource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);

        Assert.Equal(expectedMethodsDosaiSource.Length + expectedMethodsAssemblySource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiSource);
        AssertMethods(actualMethods, expectedMethodsAssemblySource);
        Directory.Delete(sourceDirectory, true);
    }

    [Fact]
    public void GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(combinedDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(combinedDirectory, DosaiDLL), true);
        File.Copy(DosaiSource, Path.Combine(combinedDirectory, DosaiSource), true);
        File.Copy(AssemblySource, Path.Combine(combinedDirectory, AssemblySource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var actualMethods = JsonSerializer.Deserialize<List<Method>>(result);

        if(actualMethods != null)
        {
            actualMethods = FilterOutTestMethods(actualMethods);
        }

        Assert.Equal(expectedMethodsDosaiTestsDLL.Length + 
                     expectedMethodsDosaiDLL.Length + 
                     expectedMethodsAssemblySource.Length +
                     expectedMethodsDosaiSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestsDLL);
        AssertMethods(actualMethods, expectedMethodsDosaiDLL);
        AssertMethods(actualMethods, expectedMethodsDosaiSource);
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
    #endregion Methods

    #region Dependencies
    [Fact]
    public void GetDependencies_Assembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiDLL);
        var result = Depscan.Dosai.GetDependencies(assemblyPath);
        var actualDependenciess = JsonSerializer.Deserialize<List<Dependency>>(result);
        Assert.Equal(expectedDependenciesDosaiDLL.Length, actualDependenciess?.Count);
        AssertDependencies(actualDependenciess, expectedDependenciesDosaiDLL);
    }

    [Fact]
    public void GetDependencies_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(DosaiSource);
        var result = Depscan.Dosai.GetDependencies(sourcePath);
        var actualDependencies = JsonSerializer.Deserialize<List<Dependency>>(result);
        Assert.Equal(expectedDependenciesDosaiSource.Length, actualDependencies?.Count);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiSource);
    }

    [Fact]
    public void GetDependencies_Assembly_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(assembliesDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(assembliesDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(assembliesDirectory, DosaiDLL), true);

        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), assembliesDirectory);
        var result = Depscan.Dosai.GetDependencies(assemblyFolder);
        var actualDependencies = JsonSerializer.Deserialize<List<Dependency>>(result);
        Assert.Equal(expectedDependenciesDosaiTestsDLL.Length + expectedDependenciesDosaiDLL.Length, actualDependencies?.Count);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiTestsDLL);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiDLL);
        Directory.Delete(assembliesDirectory, true);
    }

    [Fact]
    public void GetDependencies_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(sourceDirectory);
        File.Copy(DosaiSource, Path.Combine(sourceDirectory, DosaiSource), true);
        File.Copy(AssemblySource, Path.Combine(sourceDirectory, AssemblySource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetDependencies(sourceFolder);
        var actualDependencies = JsonSerializer.Deserialize<List<Dependency>>(result);
        Assert.Equal(expectedDependenciesDosaiSource.Length + expectedDependenciesAssemblySource.Length, actualDependencies?.Count);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiSource);
        AssertDependencies(actualDependencies, expectedDependenciesAssemblySource);
        Directory.Delete(sourceDirectory, true);
    }

    [Fact]
    public void GetDependencies_AssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestsDLL, Path.Combine(combinedDirectory, DosaiTestsDLL), true);
        File.Copy(DosaiDLL, Path.Combine(combinedDirectory, DosaiDLL), true);
        File.Copy(DosaiSource, Path.Combine(combinedDirectory, DosaiSource), true);
        File.Copy(AssemblySource, Path.Combine(combinedDirectory, AssemblySource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetDependencies(folder);
        var actualDependencies = JsonSerializer.Deserialize<List<Dependency>>(result);
        Assert.Equal(expectedDependenciesDosaiTestsDLL.Length + 
                     expectedDependenciesDosaiDLL.Length + 
                     expectedDependenciesAssemblySource.Length +
                     expectedDependenciesDosaiSource.Length, actualDependencies?.Count);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiTestsDLL);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiDLL);
        AssertDependencies(actualDependencies, expectedDependenciesDosaiSource);
        AssertDependencies(actualDependencies, expectedDependenciesAssemblySource);
        Directory.Delete(combinedDirectory, true);
    }

    [Fact]
    public void GetDependencies_PathIsNotDLLFile_ThrowsException()
    {
        var assemblyPath = GetFilePath(DosaiTestsPdb);
        Assert.Throws<Exception>(() => Depscan.Dosai.GetDependencies(assemblyPath));
    }

    [Fact]
    public void GetDependencies_PathDoesNotExist_ThrowsException()
    {
        var assemblyPath = GetFilePath(FakeDLL);
        Assert.Throws<FileNotFoundException>(() => Depscan.Dosai.GetDependencies(assemblyPath));
    }

    [Fact]
    public void GetDependencies_PathIsEmptyDirectory_ReturnsNothing()
    {
        Directory.CreateDirectory(emptyDirectory);
        var assemblyFolder = Path.Combine(Directory.GetCurrentDirectory(), emptyDirectory);
        var result = Depscan.Dosai.GetDependencies(assemblyFolder);
        var dependencies = JsonSerializer.Deserialize<List<Dependency>>(result);
        Assert.Equal(0, dependencies?.Count);
    }
    #endregion Dependencies

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
                troubleshootingMessage = $"{expectedMethod.FileName}.{expectedMethod.Namespace}.{expectedMethod.Class}.{expectedMethod.Attributes}.{expectedMethod.Name}.{expectedMethod.ReturnType}.{expectedMethod.LineNumber}.{expectedMethod.ColumnNumber}";
                matchingMethod = actualMethods?.Single(method => method.FileName == expectedMethod.FileName &&
                                                  method.Namespace == expectedMethod.Namespace &&
                                                  method.Class == expectedMethod.Class &&
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

    private static void AssertDependencies(List<Dependency>? actualDependencies, Dependency[] expectedDependencies)
    {
        foreach(var expectedDependency in expectedDependencies)
        {
            Dependency? matchingDependency = null;
            var troubleshootingMessage = string.Empty;

            try
            {
                troubleshootingMessage = $"{expectedDependency.FileName}.{expectedDependency.Name}.{expectedDependency.LineNumber}.{expectedDependency.ColumnNumber}";
                matchingDependency = actualDependencies?.Single(dependency => dependency.FileName == expectedDependency.FileName &&
                                                  dependency.Name == expectedDependency.Name &&
                                                  dependency.LineNumber == expectedDependency.LineNumber &&
                                                  dependency.ColumnNumber == expectedDependency.ColumnNumber);

                Assert.NotNull(matchingDependency);
            }
            catch(Exception)
            {
                Assert.Fail($"Matching dependency not found. Expecting: {troubleshootingMessage}");
            }
        }
    }

    private static string GetFilePath(string filePath)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        return Path.Join(currentDirectory, filePath);
    }

    // We want to use the Dosai Tests DLL in unit testing because it has Assembly.cs for test cases, but exclude the unit tests themselves as they can change often, requiring extra unnecessary work to update tests to be passing
    private static List<Method> FilterOutTestMethods(List<Method> methods)
    {
        return methods.Where(method => method.Namespace != DosaiTestsNamespace).ToList();
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

    // Expected methods in Dosai.Tests.dll, with the unit test methods themselves excluded
    private static readonly Method[] expectedMethodsDosaiTestsDLL =
    [
        new()
        {
            FileName = DosaiTestsDLL,
            Namespace = FooBarNamespace,
            Class = "Foo",
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
            Class = "Bar",
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
            Class = "Hello",
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
            Class = "Hello",
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
            Class = "World",
            Attributes = "Public, HideBySig",
            Name = "shout",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        }
    ];

    // Expected methods in Dosai.cs
    private static readonly Method[] expectedMethodsDosaiSource =
    [
        new()
        {
            FileName = DosaiSource,
            Namespace = DepscanNamespace,
            Class = "Dosai",
            Attributes = "Public, Static",
            Name = "GetNamespaces",
            ReturnType = String,
            LineNumber = 26,
            ColumnNumber = 4,
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
            FileName = DosaiSource,
            Namespace = DepscanNamespace,
            Class = "Dosai",
            Attributes = "Public, Static",
            Name = "GetMethods",
            ReturnType = String,
            LineNumber = 136,
            ColumnNumber = 4,
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
            FileName = DosaiSource,
            Namespace = DepscanNamespace,
            Class = "Dosai",
            Attributes = "Public, Static",
            Name = "GetDependencies",
            ReturnType = String,
            LineNumber = 260,
            ColumnNumber = 4,
            Parameters =
            [
                new()
                {
                    Name = "path",
                    Type = String
                }
            ]
        }
    ];

    // Expected methods in Dosai.dll
    private static readonly Method[] expectedMethodsDosaiDLL =
    [
        new()
        {
            FileName = DosaiDLL,
            Namespace = DepscanNamespace,
            Class = "Dosai",
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
            Class = "Dosai",
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
            Class = "Dosai",
            Attributes = "Public, Static, HideBySig",
            Name = "GetDependencies",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Method",
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
            Class = "Namespace",
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
            Class = "Namespace",
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
            Class = "Namespace",
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
            Class = "Namespace",
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
            Class = "Parameter",
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
            Class = "Parameter",
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
            Class = "Parameter",
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
            Class = "Parameter",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "Dependency",
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
            Class = "CommandLine",
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
            Class = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = Void,
            LineNumber = 13,
            ColumnNumber = 8,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = HelloWorldNamespace,
            Class = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 18,
            ColumnNumber = 8,
            ReturnType = Task,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = HelloWorldNamespace,
            Class = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = Void,
            LineNumber = 26,
            ColumnNumber = 8,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = FooBarNamespace,
            Class = "Foo",
            Attributes = "Public",
            Name = "foo",
            ReturnType = Void,
            LineNumber = 42,
            ColumnNumber = 8,
            Parameters = []
        },
        new()
        {
            FileName = AssemblySource,
            Namespace = FooBarNamespace,
            Class = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = Void,
            LineNumber = 50,
            ColumnNumber = 8,
            Parameters = []
        }
    ];

    // Expected dependencies in Assembly.cs
    private static readonly Dependency[] expectedDependenciesAssemblySource =
    [
        new()
        {
            FileName = AssemblySource,
            Name = "Microsoft.CodeAnalysis",
            LineNumber = 1,
            ColumnNumber = 0
        },
        new()
        {
            FileName = AssemblySource,
            Name = "Microsoft.CodeAnalysis.CSharp",
            LineNumber = 2,
            ColumnNumber = 0
        },
        new()
        {
            FileName = AssemblySource,
            Name = "Microsoft.CodeAnalysis.CSharp.Syntax",
            LineNumber = 3,
            ColumnNumber = 0
        },
        new()
        {
            FileName = AssemblySource,
            Name = "System.Globalization",
            LineNumber = 4,
            ColumnNumber = 0
        },
        new()
        {
            FileName = AssemblySource,
            Name = "System.Reflection",
            LineNumber = 5,
            ColumnNumber = 0
        },
        new()
        {
            FileName = AssemblySource,
            Name = "System.Text.Json",
            LineNumber = 6,
            ColumnNumber = 0
        }
    ];

    // Expected dependencies in Dosai.cs
    private static readonly Dependency[] expectedDependenciesDosaiSource =
    [
        new()
        {
            FileName = DosaiSource,
            Name = "Microsoft.CodeAnalysis",
            LineNumber = 1,
            ColumnNumber = 0
        },
        new()
        {
            FileName = DosaiSource,
            Name = "Microsoft.CodeAnalysis.CSharp",
            LineNumber = 2,
            ColumnNumber = 0
        },
        new()
        {
            FileName = DosaiSource,
            Name = "Microsoft.CodeAnalysis.CSharp.Syntax",
            LineNumber = 3,
            ColumnNumber = 0
        },
        new()
        {
            FileName = DosaiSource,
            Name = "System.Globalization",
            LineNumber = 4,
            ColumnNumber = 0
        },
        new()
        {
            FileName = DosaiSource,
            Name = "System.Reflection",
            LineNumber = 5,
            ColumnNumber = 0
        },
        new()
        {
            FileName = DosaiSource,
            Name = "System.Text.Json",
            LineNumber = 6,
            ColumnNumber = 0
        }
    ];

    // Expected dependencies in Dosai.dll
    private static readonly Dependency[] expectedDependenciesDosaiDLL =
    [
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Runtime",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.CommandLine",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Text.Json",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Collections",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "Microsoft.CodeAnalysis",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "Microsoft.CodeAnalysis.CSharp",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Linq",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Console",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Collections.Immutable",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiDLL,
            Name = "System.Text.Encodings.Web",
            LineNumber = default,
            ColumnNumber = default
        }
    ];

    // Expected dependencies in Dosai.Tests.dll
    private static readonly Dependency[] expectedDependenciesDosaiTestsDLL =
    [
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "System.Runtime",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "Microsoft.VisualStudio.TestPlatform.ObjectModel",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "Dosai",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "xunit.core",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "System.Collections",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "System.Text.Json",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "xunit.assert",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "System.Linq",
            LineNumber = default,
            ColumnNumber = default
        },
        new()
        {
            FileName = DosaiTestsDLL,
            Name = "System.Runtime.InteropServices",
            LineNumber = default,
            ColumnNumber = default
        }
    ];

    private const string DosaiTestsDLL = "Dosai.Tests.dll";
    private const string DosaiTestsPdb = "Dosai.Tests.pdb";
    private const string DosaiTestsSource = "DosaiTests.cs";
    private const string DosaiSource = "Dosai.cs";
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