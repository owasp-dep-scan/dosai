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
        var assemblyPath = GetFilePath(DosaiTestDataDLL);
        var result = Depscan.Dosai.GetNamespaces(assemblyPath);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesDosaiTestDataDLL.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestDataDLL);
    }

    [Fact]
    public void GetNamespaces_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldSource);
        var result = Depscan.Dosai.GetNamespaces(sourcePath);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesHelloWorldSource.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesHelloWorldSource);
    }

    [Fact]
    public void GetNamespaces_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true);

        Directory.CreateDirectory(sourceDirectory);
        File.Copy(HelloWorldSource, Path.Combine(sourceDirectory, HelloWorldSource), true);
        File.Copy(FooBarSource, Path.Combine(sourceDirectory, FooBarSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetNamespaces(sourceFolder);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesHelloWorldSource.Length + expectedNamespacesFooBarSource.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesHelloWorldSource);
        AssertNamespaces(actualNamespaces, expectedNamespacesFooBarSource);
    }

    [Fact]
    public void GetNamespaces_AssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);

        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestDataDLL, Path.Combine(combinedDirectory, DosaiTestDataDLL), true);
        File.Copy(HelloWorldSource, Path.Combine(combinedDirectory, HelloWorldSource), true);
        File.Copy(FooBarSource, Path.Combine(combinedDirectory, FooBarSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetNamespaces(folder);
        var actualNamespaces = JsonSerializer.Deserialize<List<Namespace>>(result);
        Assert.Equal(expectedNamespacesDosaiTestDataDLL.Length +
                     expectedNamespacesHelloWorldSource.Length +
                     expectedNamespacesFooBarSource.Length, actualNamespaces?.Count);
        AssertNamespaces(actualNamespaces, expectedNamespacesDosaiTestDataDLL);
        AssertNamespaces(actualNamespaces, expectedNamespacesHelloWorldSource);
        AssertNamespaces(actualNamespaces, expectedNamespacesFooBarSource);
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
        var assemblyPath = GetFilePath(DosaiTestDataDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(expectedMethodsDosaiTestDataDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsHelloWorldSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldSource);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true);

        Directory.CreateDirectory(sourceDirectory);
        File.Copy(HelloWorldSource, Path.Combine(sourceDirectory, HelloWorldSource), true);
        File.Copy(FooBarSource, Path.Combine(sourceDirectory, FooBarSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsHelloWorldSource.Length + expectedMethodsFooBarSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldSource);
        AssertMethods(actualMethods, expectedMethodsFooBarSource);
    }

    [Fact]
    public void GetMethods_AssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);

        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestDataDLL, Path.Combine(combinedDirectory, DosaiTestDataDLL), true);
        File.Copy(HelloWorldSource, Path.Combine(combinedDirectory, HelloWorldSource), true);
        File.Copy(FooBarSource, Path.Combine(combinedDirectory, FooBarSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsDosaiTestDataDLL.Length +
                     expectedMethodsHelloWorldSource.Length +
                     expectedMethodsFooBarSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldSource);
        AssertMethods(actualMethods, expectedMethodsFooBarSource);
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
    #endregion Methods

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

    // Expected namespaces in Dosai.TestData.dll
    private static readonly Namespace[] expectedNamespacesDosaiTestDataDLL =
    [
        new()
        {
            FileName = DosaiTestDataDLL,
            Name = FooBarNamespace
        },
        new()
        {
            FileName = DosaiTestDataDLL,
            Name = HelloWorldNamespace
        }
    ];

    // Expected namespaces in HelloWorld.cs
    private static readonly Namespace[] expectedNamespacesHelloWorldSource =
    [
        new()
        {
            FileName = HelloWorldSource,
            Name = HelloWorldNamespace
        }
    ];

    // Expected namespaces in FooBar.cs
    private static readonly Namespace[] expectedNamespacesFooBarSource =
    [
        new()
        {
            FileName = FooBarSource,
            Name = FooBarNamespace
        }
    ];

    // Expected methods in Dosai.TestData.dll
    private static readonly Method[] expectedMethodsDosaiTestDataDLL =
    [
        new()
        {
            FileName = DosaiTestDataDLL,
            Namespace = FooBarNamespace,
            ClassName = "Foo",
            Attributes = "Public, Static, HideBySig",
            Name = "Main",
            ReturnType = Void,
            LineNumber = default,
            ColumnNumber = default,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "System.String[]"
                }
            ]
        },
        new()
        {
            FileName = DosaiTestDataDLL,
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
            FileName = DosaiTestDataDLL,
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
            FileName = DosaiTestDataDLL,
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
            FileName = DosaiTestDataDLL,
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

    // Expected methods in HelloWorld.cs
    private static readonly Method[] expectedMethodsHelloWorldSource =
    [
        new()
        {
            FileName = HelloWorldSource,
            Namespace = HelloWorldNamespace,
            ClassName = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = Void,
            LineNumber = 8,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = HelloWorldSource,
            Namespace = HelloWorldNamespace,
            ClassName = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 13,
            ColumnNumber = 9,
            ReturnType = Task,
            Parameters = []
        },
        new()
        {
            FileName = HelloWorldSource,
            Namespace = HelloWorldNamespace,
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = Void,
            LineNumber = 21,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in FooBar.cs
    private static readonly Method[] expectedMethodsFooBarSource =
    [
        new()
        {
            FileName = FooBarSource,
            Namespace = FooBarNamespace,
            ClassName = "Foo",
            Attributes = "Public, Static",
            Name = "Main",
            ReturnType = Void,
            LineNumber = 7,
            ColumnNumber = 9,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "String[]"
                }
            ]
        },
        new()
        {
            FileName = FooBarSource,
            Namespace = FooBarNamespace,
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = Void,
            LineNumber = 15,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    private const string DosaiTestDataDLL = "Dosai.TestData.dll";
    private const string DosaiTestsPdb = "Dosai.Tests.pdb";
    private const string HelloWorldSource = "HelloWorld.cs";
    private const string FooBarSource = "FooBar.cs";
    private const string FakeDLL = "Fake.dll";
    private const string FooBarNamespace = "FooBar";
    private const string HelloWorldNamespace = "HelloWorld";
    private const string Void = "Void";
    private const string Task = "Task";
    private const string assembliesDirectory = "assemblies";
    private const string sourceDirectory = "source";
    private const string emptyDirectory = "empty";
    private const string combinedDirectory = "combined";
}