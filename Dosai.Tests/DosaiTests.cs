using Depscan;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(36, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataCSharpDLL);
    }

    [Fact]
    public void GetMethods_VBAssembly_PathIsFile_ReturnsDetails()
    {
        var assemblyPath = GetFilePath(DosaiTestDataVBDLL);
        var result = Depscan.Dosai.GetMethods(assemblyPath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        
        Assert.Equal(24, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataVBDLL);
    }

    [Fact]
    public void GetMethods_CSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldCSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;
        var properties = methodsSlice?.Properties;
        var fields = methodsSlice?.Fields;
        Assert.Equal(21, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        var genericProcessorClassMethods = actualMethods?.Where(m => m.ClassName == "GenericProcessor").ToList();
        Assert.NotNull(genericProcessorClassMethods);
        Assert.True(genericProcessorClassMethods?.Count == 4);
        var processMethod = genericProcessorClassMethods?.FirstOrDefault(m => m.Name == "Process");
        Assert.NotNull(processMethod);
        Assert.True(processMethod?.IsGenericMethod == false);
        Assert.True(processMethod?.Parameters?.Count == 1);
        var convertToMethod = genericProcessorClassMethods?.FirstOrDefault(m => m.Name == "ConvertTo");
        Assert.NotNull(convertToMethod);
        Assert.True(convertToMethod.IsGenericMethod); 
        Assert.Single(convertToMethod.GenericParameters!);
        Assert.Contains("TResult", convertToMethod?.GenericParameters ?? []);
        Assert.True(convertToMethod?.Parameters?.Count == 1);
        var utilityClassMethods = actualMethods?.Where(m => m.ClassName == "Utility").ToList();
        Assert.NotNull(utilityClassMethods);
        var getDefaultMethod = utilityClassMethods?.FirstOrDefault(m => m.Name == "GetDefault");
        Assert.NotNull(getDefaultMethod);
        Assert.True(getDefaultMethod?.IsGenericMethod);
        Assert.Equal(1, getDefaultMethod?.GenericParameters?.Count);
        Assert.Contains("T", getDefaultMethod?.GenericParameters ?? []);
        Assert.Equal(0, getDefaultMethod?.Parameters?.Count);
        var swapMethod = utilityClassMethods?.FirstOrDefault(m => m.Name == "Swap");
        Assert.NotNull(swapMethod);
        Assert.True(swapMethod.IsGenericMethod); 
        Assert.True(swapMethod?.GenericParameters?.Count == 1);
        Assert.Contains("T", swapMethod?.GenericParameters ?? []);
        Assert.True(swapMethod?.Parameters?.Count == 2);
        Assert.Equal("T", swapMethod?.Parameters?[0].Type);
        Assert.Equal("T", swapMethod?.Parameters?[1].Type);
        Assert.Equal("void", swapMethod?.ReturnType);
        var genericProcessorProperties = properties?.Where(p => p.ClassName == "GenericProcessor").ToList();
        Assert.NotNull(genericProcessorProperties);
        var valueProperty = genericProcessorProperties?.FirstOrDefault(p => p.Name == "Value");
        Assert.NotNull(valueProperty);
        Assert.Equal("T", valueProperty?.Type);
        Assert.Equal("T", valueProperty?.TypeFullName);
        // Test inheritance and interface implementation
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
        var getNamesMethod = helloClassMethods?.FirstOrDefault(m => m.Name == "GetNames");
        Assert.NotNull(getNamesMethod);
        Assert.Equal("System.Collections.Generic.List<string>", getNamesMethod.ReturnType);
        Assert.True(genericProcessorClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IGenericInterface")));
        
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
        
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_VBSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldVBSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(13, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
    
        // Test inheritance and interface implementation for VB.NET
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
    
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
    
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
    
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_FSharpSource_PathIsFile_ReturnsDetails()
    {
        var sourcePath = GetFilePath(HelloWorldFSharpSource);
        var result = Depscan.Dosai.GetMethods(sourcePath);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;

        // We expect at least some methods to be detected
        Assert.True(actualMethods?.Count > 0);
        // Check that we have the expected F# functions
        Assert.Contains(actualMethods, m => m.Name == "hello");
        Assert.Contains(actualMethods, m => m.Name == "goodbye");
        Assert.Contains(actualMethods, m => m.Name == "add");
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "Introduce");
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "CelebrateBirthday");
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
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(24, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        AssertMethods(actualMethods, expectedMethodsFooBarCSharpSource);
        
        // Test inheritance and interface implementation
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
        
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
        
        // Test method calls information
        AssertMethodCalls(methodCalls);
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
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(16, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
        AssertMethods(actualMethods, expectedMethodsFooBarVBSource);
    
        // Test inheritance and interface implementation for VB.NET
        var helloClassMethods = actualMethods?.Where(m => m.ClassName == "Hello").ToList();
        var worldClassMethods = actualMethods?.Where(m => m.ClassName == "World").ToList();
    
        // Check that Hello class has inheritance info
        Assert.True(helloClassMethods?.Any(m => m.BaseType == "BaseClass"));
        Assert.True(helloClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
    
        // Check that World class has interface info
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("ITestInterface")));
        Assert.True(worldClassMethods?.Any(m => m.ImplementedInterfaces is not null && m.ImplementedInterfaces.Contains("IAnotherInterface")));
    
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_FSharpSource_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(fsharpSourceDirectory)) Directory.Delete(fsharpSourceDirectory, true);

        Directory.CreateDirectory(fsharpSourceDirectory);
        File.Copy(HelloWorldFSharpSource, Path.Combine(fsharpSourceDirectory, HelloWorldFSharpSource), true);

        var sourceFolder = Path.Combine(Directory.GetCurrentDirectory(), fsharpSourceDirectory);
        var result = Depscan.Dosai.GetMethods(sourceFolder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;

        // We expect at least some methods to be detected
        Assert.True(actualMethods?.Count > 0);
        // Check that we have the expected F# functions
        Assert.Contains(actualMethods, m => m.Name == "hello");
        Assert.Contains(actualMethods, m => m.Name == "goodbye");
        Assert.Contains(actualMethods, m => m.Name == "add");
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "Introduce");
        Assert.Contains(actualMethods, m => m.ClassName == "Person" && m.Name == "CelebrateBirthday");
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
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(60, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataCSharpDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldCSharpSource);
        AssertMethods(actualMethods, expectedMethodsFooBarCSharpSource);
        
        // Test method calls information
        AssertMethodCalls(methodCalls);
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
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;
        var methodCalls = methodsSlice?.MethodCalls;

        Assert.Equal(40, actualMethods?.Count);
        AssertMethods(actualMethods, expectedMethodsDosaiTestDataVBDLL);
        AssertMethods(actualMethods, expectedMethodsHelloWorldVBSource);
        AssertMethods(actualMethods, expectedMethodsFooBarVBSource);
    
        // Test method calls information
        AssertMethodCalls(methodCalls);
    }

    [Fact]
    public void GetMethods_AllLanguages_PathIsDirectory_ReturnsDetails()
    {
        if(Directory.Exists(allLanguagesDirectory)) Directory.Delete(allLanguagesDirectory, true);

        Directory.CreateDirectory(allLanguagesDirectory);
        File.Copy(DosaiTestDataCSharpDLL, Path.Combine(allLanguagesDirectory, DosaiTestDataCSharpDLL), true);
        File.Copy(DosaiTestDataVBDLL, Path.Combine(allLanguagesDirectory, DosaiTestDataVBDLL), true);
        File.Copy(HelloWorldCSharpSource, Path.Combine(allLanguagesDirectory, HelloWorldCSharpSource), true);
        File.Copy(FooBarCSharpSource, Path.Combine(allLanguagesDirectory, FooBarCSharpSource), true);
        File.Copy(HelloWorldVBSource, Path.Combine(allLanguagesDirectory, HelloWorldVBSource), true);
        File.Copy(FooBarVBSource, Path.Combine(allLanguagesDirectory, FooBarVBSource), true);
        File.Copy(HelloWorldFSharpSource, Path.Combine(allLanguagesDirectory, HelloWorldFSharpSource), true);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), allLanguagesDirectory);
        var result = Depscan.Dosai.GetMethods(folder);
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
        var actualMethods = methodsSlice?.Methods;

        Assert.True(actualMethods?.Count > 0);
        // Check that we have methods from all languages
        Assert.Contains(actualMethods, m => m.FileName == DosaiTestDataCSharpDLL);
        Assert.Contains(actualMethods, m => m.FileName == DosaiTestDataVBDLL);
        Assert.Contains(actualMethods, m => m.FileName == HelloWorldCSharpSource);
        Assert.Contains(actualMethods, m => m.FileName == HelloWorldVBSource);
        Assert.Contains(actualMethods, m => m.FileName == HelloWorldFSharpSource);
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
        var deserializeOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() } 
        };
        var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result,  deserializeOptions);
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
            if (expectedMethod.Name == ".ctor")
            {
                continue;
            }
        
            var matchingMethod = actualMethods?.FirstOrDefault(method => 
                method.FileName == expectedMethod.FileName &&
                method.Namespace == expectedMethod.Namespace &&
                method.ClassName == expectedMethod.ClassName &&
                method.Name == expectedMethod.Name);

            Assert.NotNull(matchingMethod);

            if (expectedMethod.Parameters is not null)
            {
                foreach (var expectedParameter in expectedMethod.Parameters)
                {
                    Assert.True(matchingMethod?.Parameters?.Exists(parameter => parameter.Name == expectedParameter.Name &&
                        parameter.Type == expectedParameter.Type));
                }
            }
        }
    }
    
    private static void AssertMethodCalls(List<MethodCalls>? actualMethodCalls)
    {
        Assert.NotNull(actualMethodCalls);
        if (actualMethodCalls?.Count > 0)
        {
            foreach (var methodCall in actualMethodCalls)
            {
                Assert.NotNull(methodCall.FileName);
                Assert.NotNull(methodCall.CalledMethod);
                Assert.True(methodCall.LineNumber > 0);
                Assert.True(methodCall.ColumnNumber > 0);
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
            LineNumber = 19,
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
            LineNumber = 24,
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
            ClassName = "Hello",
            Attributes = "Public",
            Name = "InterfaceMethod",
            ReturnType = "Void",
            LineNumber = 29,
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
            Attributes = "Public, Override",
            Name = "BaseMethod",
            ReturnType = "Void",
            LineNumber = 33,
            ColumnNumber = 9,
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
            Name = "InterfaceMethod",
            ReturnType = "Void",
            LineNumber = 43,
            ColumnNumber = 9,
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
            LineNumber = 39,
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
    private const string HelloWorldFSharpSource = "HelloWorld.fs";
    private const string FakeDLL = "Fake.dll";
    private const string sourceDirectory = "source";
    private const string fsharpSourceDirectory = "fsharp-source";
    private const string emptyDirectory = "empty";
    private const string combinedDirectory = "combined";
    private const string allLanguagesDirectory = "all-languages";
}