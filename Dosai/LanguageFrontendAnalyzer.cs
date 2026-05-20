using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Depscan;

public static partial class LanguageFrontendAnalyzer
{
    private const string FrontendModule = "LanguageFrontend";
    private const string RNativeParserModule = "R.NativeParser";
    private static readonly TimeSpan DefaultRParserTimeout = TimeSpan.FromSeconds(10);

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

    public static bool IsFSharpCompilerServiceAvailable => TryLoadFSharpCompilerService() is not null;

    public static bool IsRNativeParserAvailable => ResolveExecutable("Rscript") is not null;

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
        const string moduleName = FrontendModule;
        var lines = File.ReadAllLines(file);
        var namespaceName = "Global";
        var className = "Module";
        string? currentSourceId = null;
        var currentDeclarationIndent = int.MaxValue;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line.Length - line.TrimStart().Length;
            var module = FSharpNamespaceOrModule().Match(line);
            if (module.Success)
            {
                namespaceName = module.Groups[1].Value;
                className = namespaceName.Split('.').LastOrDefault() ?? "Module";
                currentDeclarationIndent = int.MaxValue;
                dependencies.Add(CreateDependency(basePath, file, namespaceName, namespaceName, i + 1, Math.Max(1, line.IndexOf(namespaceName, StringComparison.Ordinal) + 1)));
                continue;
            }
            var type = FSharpType().Match(line);
            if (type.Success)
            {
                className = type.Groups[1].Value;
                currentDeclarationIndent = int.MaxValue;
                continue;
            }
            var function = FSharpFunction().Match(line);
            if (function.Success && !IsKeyword(function.Groups[1].Value))
            {
                if (currentSourceId is not null && indent > currentDeclarationIndent)
                {
                    continue;
                }

                var name = function.Groups[1].Value;
                currentSourceId = CreateId(namespaceName, className, name, file, i + 1);
                currentDeclarationIndent = indent;
                methods.Add(CreateMethod(basePath, file, namespaceName, className, name, moduleName, currentSourceId, i + 1, Math.Max(1, line.IndexOf(name, StringComparison.Ordinal) + 1)));
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
        if (TryAnalyzeRWithNativeParser(basePath, file, methods, dependencies, calls))
        {
            return;
        }

        var lines = File.ReadAllLines(file);
        var currentFunction = "script";
        var currentSourceId = CreateId("R", Path.GetFileNameWithoutExtension(file), currentFunction, file, 1);
        methods.Add(CreateMethod(basePath, file, "R", Path.GetFileNameWithoutExtension(file), currentFunction, FrontendModule, currentSourceId, 1, 1));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var function = RFunction().Match(line);
            if (function.Success)
            {
                currentFunction = function.Groups[1].Value;
                currentSourceId = CreateId("R", Path.GetFileNameWithoutExtension(file), currentFunction, file, i + 1);
                methods.Add(CreateMethod(basePath, file, "R", Path.GetFileNameWithoutExtension(file), currentFunction, FrontendModule, currentSourceId, i + 1, Math.Max(1, line.IndexOf(currentFunction, StringComparison.Ordinal) + 1)));
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
            var callScanOffset = 0;
            if (function.Success)
            {
                className = function.Groups[1].Success ? function.Groups[1].Value : className;
                currentFunction = function.Groups[2].Value;
                currentSourceId = CreateId("C++", className, currentFunction, file, i + 1);
                methods.Add(CreateMethod(basePath, file, "C++", className, currentFunction, "VC++", currentSourceId, i + 1, Math.Max(1, line.IndexOf(currentFunction, StringComparison.Ordinal) + 1)));

                var bodyStart = line.IndexOf('{', StringComparison.Ordinal);
                if (bodyStart < 0)
                {
                    continue;
                }

                callScanOffset = bodyStart + 1;
            }

            foreach (Match call in CppCall().Matches(line, callScanOffset))
            {
                var name = call.Groups["name"].Value;
                if (IsKeyword(name)) continue;
                calls.Add(CreateCall(basePath, file, currentSourceId, "C++", className, name, i + 1, call.Index + 1));
            }
        }
    }

    private static bool TryAnalyzeRWithNativeParser(string basePath, string file, List<Method> methods, List<Dependency> dependencies, List<MethodCalls> calls)
    {
        var rscript = ResolveExecutable("Rscript");
        if (rscript is null)
        {
            return false;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"dosai-r-parse-{Guid.NewGuid():N}.R");
        try
        {
            File.WriteAllText(scriptPath, """
args <- commandArgs(trailingOnly = TRUE)
pd <- getParseData(parse(file = args[[1]], keep.source = TRUE))
if (is.null(pd)) quit(status = 0)
pd <- pd[, c("line1", "col1", "token", "text", "parent", "id")]
write.table(pd, file = "", sep = "\t", row.names = FALSE, col.names = TRUE, quote = FALSE, na = "")
""");
            var start = new ProcessStartInfo(rscript, $"{Quote(scriptPath)} {Quote(file)}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is null) return false;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)RParserTimeout.TotalMilliseconds))
            {
                KillProcess(process);
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return false;
            }

            var rows = ParseRParseData(stdout).ToList();
            var className = Path.GetFileNameWithoutExtension(file);
            var scriptId = CreateId("R", className, "script", file, 1);
            methods.Add(CreateMethod(basePath, file, "R", className, "script", RNativeParserModule, scriptId, 1, 1));

            foreach (var fn in InferRFunctions(rows))
            {
                methods.Add(CreateMethod(basePath, file, "R", className, fn.Name, RNativeParserModule, CreateId("R", className, fn.Name, file, fn.Line), fn.Line, fn.Column));
            }

            var sourceId = scriptId;
            foreach (var row in rows.OrderBy(row => row.Line).ThenBy(row => row.Column))
            {
                var matchingFunction = methods.Where(method => method.Module == RNativeParserModule && method.Name != "script" && method.LineNumber <= row.Line).OrderByDescending(method => method.LineNumber).FirstOrDefault();
                if (matchingFunction?.SourceSignature is not null)
                {
                    sourceId = matchingFunction.SourceSignature;
                }

                if (row.Token == "SYMBOL_FUNCTION_CALL" && !IsKeyword(row.Text))
                {
                    calls.Add(CreateCall(basePath, file, sourceId, "R", className, row.Text, row.Line, row.Column));
                }

                if (row is { Token: "SYMBOL_FUNCTION_CALL", Text: "library" or "require" })
                {
                    var package = rows.FirstOrDefault(candidate => candidate.Line == row.Line && candidate.Column > row.Column && candidate.Token is "SYMBOL" or "STR_CONST")?.Text.Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(package))
                    {
                        dependencies.Add(CreateDependency(basePath, file, package, package, row.Line, row.Column, RNativeParserModule));
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); }
            catch (IOException) { }
        }
    }

    private static TimeSpan RParserTimeout
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS");
            return int.TryParse(configured, out var milliseconds) && milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : DefaultRParserTimeout;
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static IEnumerable<RParseRow> ParseRParseData(string stdout)
    {
        foreach (var line in stdout.Split('\n').Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 6 || !int.TryParse(parts[0], out var lineNumber) || !int.TryParse(parts[1], out var column)) continue;
            yield return new RParseRow(lineNumber, column, parts[2], parts[3], parts[4], parts[5]);
        }
    }

    private static IEnumerable<(string Name, int Line, int Column)> InferRFunctions(List<RParseRow> rows)
    {
        foreach (var functionToken in rows.Where(row => row.Token == "FUNCTION"))
        {
            var name = rows
                .Where(row => row.Line <= functionToken.Line && row.Token == "SYMBOL")
                .OrderByDescending(row => row.Line)
                .ThenByDescending(row => row.Column)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(name?.Text))
            {
                yield return (name.Text, name.Line, name.Column);
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

    private static Dependency CreateDependency(string basePath, string file, string name, string namespaceName, int line, int column, string module = FrontendModule) => new()
    {
        Path = SafeRelative(basePath, file),
        FileName = Path.GetFileName(file),
        Name = name,
        Namespace = namespaceName,
        Assembly = "Source",
        Module = module,
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
            Module = FrontendModule,
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
            .Where(file => extensions.Contains(Path.GetExtension(file)));
    }

    private static bool IsKeyword(string word)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "then", "else", "elif", "for", "while", "do", "match", "with", "try", "catch", "finally", "let", "rec", "and", "fun", "function", "in", "open", "module", "type", "namespace", "return", "static", "new", "NULL", "nullptr", "sizeof", "switch", "case", "library", "require" 
        };
        return keywords.Contains(word);
    }

    private static string SafeRelative(string basePath, string file) => Directory.Exists(basePath) ? Path.GetRelativePath(basePath, file) : Path.GetFileName(file);

    private static Assembly? TryLoadFSharpCompilerService()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "FSharp.Compiler.Service") ?? Assembly.Load("FSharp.Compiler.Service");
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static string? ResolveExecutable(string name)
    {
        var candidates = name.Equals("Rscript", StringComparison.OrdinalIgnoreCase) ? new[] { name, "Rscript", "rscript" } : [name];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator).Where(Directory.Exists))
        {
            foreach (var executable in candidates.Distinct(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed record RParseRow(int Line, int Column, string Token, string Text, string Parent, string Id);
}
