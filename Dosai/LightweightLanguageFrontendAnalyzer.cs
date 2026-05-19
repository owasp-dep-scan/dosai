using System.Text.RegularExpressions;

namespace Depscan;

public static partial class LightweightLanguageFrontendAnalyzer
{
    [GeneratedRegex(@"^\s*(?:namespace|module)\s+([\w\.]+)", RegexOptions.Compiled)]
    private static partial Regex FSharpNamespaceOrModule();

    [GeneratedRegex(@"^\s*type\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex FSharpType();

    [GeneratedRegex(@"^\s*(?:let|member)\s+(?:rec\s+)?(?:\w+\.)?(\w+)", RegexOptions.Compiled)]
    private static partial Regex FSharpFunction();

    [GeneratedRegex(@"^\s*([A-Za-z_][\w\.]*)\s*(?:<-|=)\s*function\s*\(", RegexOptions.Compiled)]
    private static partial Regex RFunction();

    [GeneratedRegex(@"(?<name>[A-Za-z_][\w\.:]*)\s*\(", RegexOptions.Compiled)]
    private static partial Regex RCall();

    [GeneratedRegex(@"^\s*(?:[\w:<>,~*&]+\s+)+(?:([A-Za-z_][\w]*)::)?([A-Za-z_~][\w~]*)\s*\([^;]*\)\s*(?:const\s*)?(?:\{|$)", RegexOptions.Compiled)]
    private static partial Regex CppFunction();

    [GeneratedRegex(@"(?<name>[A-Za-z_][\w:]*)(?:\s*<[^>]+>)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex CppCall();

    public static (List<Method> Methods, List<Dependency> Dependencies, List<MethodCalls> MethodCalls) GetMethods(string path, bool includeFSharp = true)
    {
        var methods = new List<Method>();
        var dependencies = new List<Dependency>();
        var methodCalls = new List<MethodCalls>();
        foreach (var file in GetFiles(path))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".fs" or ".fsi" or ".fsx")
            {
                if (!includeFSharp) continue;
                AnalyzeFSharp(path, file, methods, dependencies, methodCalls);
            }
            else if (extension is ".r" or ".rmd" or ".qmd")
            {
                AnalyzeR(path, file, methods, dependencies, methodCalls);
            }
            else if (extension is ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh")
            {
                AnalyzeCpp(path, file, methods, dependencies, methodCalls);
            }
        }

        return (methods, dependencies, methodCalls);
    }

    private static void AnalyzeFSharp(string basePath, string file, List<Method> methods, List<Dependency> dependencies, List<MethodCalls> calls)
    {
        var lines = File.ReadAllLines(file);
        var namespaceName = "Global";
        var className = "Module";
        string? currentSourceId = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var module = FSharpNamespaceOrModule().Match(line);
            if (module.Success)
            {
                namespaceName = module.Groups[1].Value;
                className = namespaceName.Split('.').LastOrDefault() ?? "Module";
                dependencies.Add(CreateDependency(basePath, file, namespaceName, namespaceName, i + 1, Math.Max(1, line.IndexOf(namespaceName, StringComparison.Ordinal) + 1)));
                continue;
            }
            var type = FSharpType().Match(line);
            if (type.Success)
            {
                className = type.Groups[1].Value;
                continue;
            }
            var function = FSharpFunction().Match(line);
            if (function.Success && !IsKeyword(function.Groups[1].Value))
            {
                var name = function.Groups[1].Value;
                currentSourceId = CreateId(namespaceName, className, name, file, i + 1);
                methods.Add(CreateMethod(basePath, file, namespaceName, className, name, "FSharp", currentSourceId, i + 1, Math.Max(1, line.IndexOf(name, StringComparison.Ordinal) + 1)));
            }

            foreach (Match call in Regex.Matches(line, @"\b([A-Za-z_][\w\.]*)\s+(?:\(|\""|[A-Za-z0-9_])"))
            {
                var name = call.Groups[1].Value.Split('.').Last();
                if (currentSourceId is null || IsKeyword(name)) continue;
                calls.Add(CreateCall(basePath, file, currentSourceId, namespaceName, className, name, i + 1, call.Index + 1));
            }
        }
    }

    private static void AnalyzeR(string basePath, string file, List<Method> methods, List<Dependency> dependencies, List<MethodCalls> calls)
    {
        var lines = File.ReadAllLines(file);
        var currentFunction = "script";
        var currentSourceId = CreateId("R", Path.GetFileNameWithoutExtension(file), currentFunction, file, 1);
        methods.Add(CreateMethod(basePath, file, "R", Path.GetFileNameWithoutExtension(file), currentFunction, "R", currentSourceId, 1, 1));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var function = RFunction().Match(line);
            if (function.Success)
            {
                currentFunction = function.Groups[1].Value;
                currentSourceId = CreateId("R", Path.GetFileNameWithoutExtension(file), currentFunction, file, i + 1);
                methods.Add(CreateMethod(basePath, file, "R", Path.GetFileNameWithoutExtension(file), currentFunction, "R", currentSourceId, i + 1, Math.Max(1, line.IndexOf(currentFunction, StringComparison.Ordinal) + 1)));
            }

            foreach (Match library in Regex.Matches(line, @"\b(?:library|require)\s*\(\s*['\"" ]?([A-Za-z0-9_.]+)"))
            {
                dependencies.Add(CreateDependency(basePath, file, library.Groups[1].Value, library.Groups[1].Value, i + 1, library.Index + 1));
            }

            foreach (Match call in RCall().Matches(line))
            {
                var name = call.Groups["name"].Value;
                if (IsKeyword(name)) continue;
                calls.Add(CreateCall(basePath, file, currentSourceId, "R", Path.GetFileNameWithoutExtension(file), name, i + 1, call.Index + 1));
            }
        }
    }

    private static void AnalyzeCpp(string basePath, string file, List<Method> methods, List<Dependency> dependencies, List<MethodCalls> calls)
    {
        var lines = File.ReadAllLines(file);
        var className = Path.GetFileNameWithoutExtension(file);
        var currentFunction = "translation_unit";
        var currentSourceId = CreateId("C++", className, currentFunction, file, 1);
        methods.Add(CreateMethod(basePath, file, "C++", className, currentFunction, "VC++", currentSourceId, 1, 1));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var include = Regex.Match(line, @"^\s*#\s*include\s*[<\"" ]([^>\"" ]+)");
            if (include.Success)
            {
                dependencies.Add(CreateDependency(basePath, file, include.Groups[1].Value, include.Groups[1].Value, i + 1, include.Index + 1));
            }

            var function = CppFunction().Match(line);
            if (function.Success)
            {
                className = function.Groups[1].Success ? function.Groups[1].Value : className;
                currentFunction = function.Groups[2].Value;
                currentSourceId = CreateId("C++", className, currentFunction, file, i + 1);
                methods.Add(CreateMethod(basePath, file, "C++", className, currentFunction, "VC++", currentSourceId, i + 1, Math.Max(1, line.IndexOf(currentFunction, StringComparison.Ordinal) + 1)));
            }

            foreach (Match call in CppCall().Matches(line))
            {
                var name = call.Groups["name"].Value;
                if (IsKeyword(name)) continue;
                calls.Add(CreateCall(basePath, file, currentSourceId, "C++", className, name, i + 1, call.Index + 1));
            }
        }
    }

    private static Method CreateMethod(string basePath, string file, string namespaceName, string className, string name, string module, string sourceSignature, int line, int column) => new()
    {
        Path = SafeRelative(basePath, file),
        FileName = Path.GetFileName(file),
        Assembly = "Source",
        Module = module,
        Namespace = namespaceName,
        ClassName = className,
        Attributes = "Public",
        Name = name,
        ReturnType = "Unknown",
        LineNumber = line,
        ColumnNumber = column,
        Parameters = [],
        CustomAttributes = [],
        SourceSignature = sourceSignature
    };

    private static Dependency CreateDependency(string basePath, string file, string name, string namespaceName, int line, int column) => new()
    {
        Path = SafeRelative(basePath, file),
        FileName = Path.GetFileName(file),
        Name = name,
        Namespace = namespaceName,
        Assembly = "Source",
        Module = "LightweightFrontend",
        LineNumber = line,
        ColumnNumber = column,
        NamespaceMembers = []
    };

    private static MethodCalls CreateCall(string basePath, string file, string sourceId, string namespaceName, string className, string targetName, int line, int column)
    {
        var targetId = $"external.{targetName}(*)";
        return new MethodCalls
        {
            Path = SafeRelative(basePath, file),
            FileName = Path.GetFileName(file),
            Assembly = "Source",
            Module = "LightweightFrontend",
            Namespace = namespaceName,
            ClassName = className,
            CalledMethod = targetName,
            LineNumber = line,
            ColumnNumber = column,
            Arguments = [],
            ArgumentExpressions = [],
            CallType = CallType.MethodCall,
            SourceId = sourceId,
            TargetId = targetId,
            CallerMethod = sourceId.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('(')[0],
            CallerNamespace = namespaceName,
            CallerClass = className,
            IsInternal = false
        };
    }

    private static string CreateId(string namespaceName, string className, string methodName, string file, int line) => $"{namespaceName}.{className}.{methodName}():Unknown@{Path.GetFileName(file)}:{line}";

    private static IEnumerable<string> GetFiles(string path)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".fs", ".fsi", ".fsx", ".r", ".rmd", ".qmd", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh" };
        if (File.Exists(path))
        {
            return extensions.Contains(Path.GetExtension(path)) ? [path] : [];
        }
        return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(file => extensions.Contains(Path.GetExtension(file)))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKeyword(string word)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "then", "else", "elif", "for", "while", "do", "match", "with", "try", "catch", "finally", "let", "rec", "and", "fun", "function", "in", "open", "module", "type", "namespace", "return", "static", "new", "NULL", "nullptr", "sizeof", "switch", "case", "library", "require", "function"
        };
        return keywords.Contains(word);
    }

    private static string SafeRelative(string basePath, string file) => Directory.Exists(basePath) ? Path.GetRelativePath(basePath, file) : Path.GetFileName(file);
}
