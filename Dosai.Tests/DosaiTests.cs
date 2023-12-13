using Depscan;
using System.Text.Json;
using Xunit;

namespace Dosai.Tests;

public class DosaiTests
{
    #region GetMethods
    [Fact]
    public void GetMethods_CSharpAssembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestDataCSharpDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(expectedMethodsDosaiTestDataCSharpDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataCSharpDLL);
    }

    [Fact]
    public void GetMethods_VBAssembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestDataVBDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(expectedMethodsDosaiTestDataVBDLL.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataVBDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldCSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsHelloWorldCSharpSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
    }

    [Fact]
    public void GetMethods_VBSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldVBSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsHelloWorldVBSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true);

        Directory.CreateDirectory(sourceDirectory);
        File.Copy(HelloWorldCSharpSource, Path.Combine(sourceDirectory, HelloWorldCSharpSource), true);
        File.Copy(FooBarCSharpSource, Path.Combine(sourceDirectory, FooBarCSharpSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsHelloWorldCSharpSource.Length + expectedMethodsFooBarCSharpSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        AssertMethods(actualMethods, expectedMethodsFooBarCSharpSource);
    }

    [Fact]
    public void GetMethods_VBSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true);

        Directory.CreateDirectory(sourceDirectory);
        File.Copy(HelloWorldVBSource, Path.Combine(sourceDirectory, HelloWorldVBSource), true);
        File.Copy(FooBarVBSource, Path.Combine(sourceDirectory, FooBarVBSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), sourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsHelloWorldVBSource.Length + expectedMethodsFooBarVBSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
        AssertMethods(actualMethods, expectedMethodsFooBarVBSource);
    }

    [Fact]
    public void GetMethods_CSharpAssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);

        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestDataCSharpDLL, Path.Combine(combinedDirectory, DosaiTestDataCSharpDLL), true);
        File.Copy(HelloWorldCSharpSource, Path.Combine(combinedDirectory, HelloWorldCSharpSource), true);
        File.Copy(FooBarCSharpSource, Path.Combine(combinedDirectory, FooBarCSharpSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsDosaiTestDataCSharpDLL.Length +
                     expectedMethodsHelloWorldCSharpSource.Length +
                     expectedMethodsFooBarCSharpSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataCSharpDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        AssertMethods(actualMethods, expectedMethodsFooBarCSharpSource);
    }

    [Fact]
    public void GetMethods_VBAssemblyAndSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);

        Directory.CreateDirectory(combinedDirectory);
        File.Copy(DosaiTestDataVBDLL, Path.Combine(combinedDirectory, DosaiTestDataVBDLL), true);
        File.Copy(HelloWorldVBSource, Path.Combine(combinedDirectory, HelloWorldVBSource), true);
        File.Copy(FooBarVBSource, Path.Combine(combinedDirectory, FooBarVBSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), combinedDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result);
        var actualMethods = methodsSlice?.Methods;

        Assert.Equal(expectedMethodsDosaiTestDataVBDLL.Length +
                     expectedMethodsHelloWorldVBSource.Length +
                     expectedMethodsFooBarVBSource.Length, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataVBDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
        AssertMethods(actualMethods, expectedMethodsFooBarVBSource);
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
    #endregion GetMethods

    private static void AssertNamespaces(List<Namespace>? actualNamespaces, Namespace[] expectedNamespaces)
    {
        foreach(var expectedNamespace in expectedNamespaces)
        {
            var troubleshootingMessage = string.Empty;

            try
            {
                troubleshootingMessage = $"{expectedNamespace.FileName}${expectedNamespace.Name}";
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
                troubleshootingMessage = $"{expectedMethod.Path}${expectedMethod.FileName}${expectedMethod.Assembly}${expectedMethod.Module}${expectedMethod.Namespace}${expectedMethod.ClassName}${expectedMethod.Attributes}${expectedMethod.Name}${expectedMethod.ReturnType}${expectedMethod.LineNumber}${expectedMethod.ColumnNumber}";
                matchingMethod = actualMethods?.Single(method => method.FileName == expectedMethod.FileName &&
                                                                 method.Assembly == expectedMethod.Assembly &&
                                                                 method.Module == expectedMethod.Module &&
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

    // Expected namespaces in Dosai.TestData.CSharp.dll
    private static readonly Namespace[] expectedNamespacesDosaiTestDataCSharpDLL =
    [
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Name = "FooBar"
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Name = "HelloWorld"
        }
    ];

    // Expected namespaces in HelloWorld.cs
    private static readonly Namespace[] expectedNamespacesHelloWorldCSharpSource =
    [
        new()
        {
            FileName = "HelloWorld.cs",
            Name = "HelloWorld"
        }
    ];

    // Expected namespaces in FooBar.cs
    private static readonly Namespace[] expectedNamespacesFooBarCSharpSource =
    [
        new()
        {
            FileName = "FooBar.cs",
            Name = "FooBar"
        }
    ];

    // Expected methods in Dosai.TestData.CSharp.dll
    private static readonly Method[] expectedMethodsDosaiTestDataCSharpDLL =
    [
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "FooBar",
            ClassName = "Foo",
            Attributes = "Public, Static, HideBySig",
            Name = "Main",
            ReturnType = "Void",
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
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "FooBar",
            ClassName = "Bar",
            Attributes = "Public, HideBySig",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Static, HideBySig",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, HideBySig",
            Name = "Appreciate",
            ReturnType = "Task",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.CSharp.dll",
            Assembly = null,
            Module = "Dosai.TestData.CSharp.dll",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public, HideBySig",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        }
    ];

    // Expected methods in Dosai.TestData.VB.dll
    private static readonly Method[] expectedMethodsDosaiTestDataVBDLL =
    [
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.FooBar",
            ClassName = "Foo",
            Attributes = "Public, Static",
            Name = "Main",
            ReturnType = "Void",
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
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.FooBar",
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.HelloWorld",
            ClassName = "Hello",
            Attributes = "Public",
            Name = "Appreciate",
            ReturnType = "Task",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        },
        new()
        {
            FileName = "Dosai.TestData.VB.dll",
            Assembly = null,
            Module = "Dosai.TestData.VB.dll",
            Namespace = "Dosai.TestData.VB.HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = default,
            ColumnNumber = default,
            Parameters = []
        }
    ];

    // Expected methods in HelloWorld.cs
    private static readonly Method[] expectedMethodsHelloWorldCSharpSource =
    [
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Static",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = 9,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 14,
            ColumnNumber = 9,
            ReturnType = "Task",
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.cs",
            Assembly = "HelloWorld.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.cs.exe",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = 22,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in HelloWorld.vb
    private static readonly Method[] expectedMethodsHelloWorldVBSource =
    [
        new()
        {
            FileName = "HelloWorld.vb",
            Assembly = "HelloWorld.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.vb.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Shared",
            Name = "elevate",
            ReturnType = "Void",
            LineNumber = 7,
            ColumnNumber = 9,
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.vb",
            Assembly = "HelloWorld.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.vb.exe",
            Namespace = "HelloWorld",
            ClassName = "Hello",
            Attributes = "Public, Async",
            Name = "Appreciate",
            LineNumber = 10,
            ColumnNumber = 9,
            ReturnType = "Task",
            Parameters = []
        },
        new()
        {
            FileName = "HelloWorld.vb",
            Assembly = "HelloWorld.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "HelloWorld.vb.exe",
            Namespace = "HelloWorld",
            ClassName = "World",
            Attributes = "Public",
            Name = "shout",
            ReturnType = "Void",
            LineNumber = 16,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in FooBar.cs
    private static readonly Method[] expectedMethodsFooBarCSharpSource =
    [
        new()
        {
            FileName = "FooBar.cs",
            Assembly = "FooBar.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.cs.exe",
            Namespace = "FooBar",
            ClassName = "Foo",
            Attributes = "Public, Static",
            Name = "Main",
            ReturnType = "Void",
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
            FileName = "FooBar.cs",
            Assembly = "FooBar.cs, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.cs.exe",
            Namespace = "FooBar",
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = 15,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    // Expected methods in FooBar.vb
    private static readonly Method[] expectedMethodsFooBarVBSource =
    [
        new()
        {
            FileName = "FooBar.vb",
            Assembly = "FooBar.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.vb.exe",
            Namespace = "FooBar",
            ClassName = "Foo",
            Attributes = "Public, Shared",
            Name = "Main",
            ReturnType = "Void",
            LineNumber = 5,
            ColumnNumber = 9,
            Parameters = [
                new()
                {
                    Name = "args",
                    Type = "String()"
                }
            ]
        },
        new()
        {
            FileName = "FooBar.vb",
            Assembly = "FooBar.vb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            Module = "FooBar.vb.exe",
            Namespace = "FooBar",
            ClassName = "Bar",
            Attributes = "Public",
            Name = "bar",
            ReturnType = "Void",
            LineNumber = 10,
            ColumnNumber = 9,
            Parameters = []
        }
    ];

    private const string DosaiTestDataCSharpDLL = "Dosai.TestData.CSharp.dll";
    private const string DosaiTestDataVBDLL = "Dosai.TestData.VB.dll";
    private const string DosaiTestsPdb = "Dosai.Tests.pdb";
    private const string HelloWorldCSharpSource = "HelloWorld.cs";
    private const string FooBarCSharpSource = "FooBar.cs";
    private const string HelloWorldVBSource = "HelloWorld.vb";
    private const string FooBarVBSource = "FooBar.vb";
    private const string FakeDLL = "Fake.dll";
    private const string sourceDirectory = "source";
    private const string emptyDirectory = "empty";
    private const string combinedDirectory = "combined";
}