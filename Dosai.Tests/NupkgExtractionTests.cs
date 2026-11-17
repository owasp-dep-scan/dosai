using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// A .nupkg is untrusted input: Dosai extracts its .NET entries into a fresh temporary
// directory before analysis, so an entry name that escapes that directory must be skipped
// and reported in Diagnostics, never written. These tests build hostile archives entry by
// entry (ZipArchive stores the FullName as the exact string given, operating system
// independent) and drive the public nupkg entry point, the smallest one that reaches the
// extraction loop.
public sealed class NupkgExtractionTests
{
    private static readonly JsonSerializerOptions SliceOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string PayloadSource = """
namespace GuardPkg
{
    public static class C
    {
        public static int Add(int a, int b) => a + b;
    }
}
""";

    [Fact]
    public void NupkgExtraction_ParentSegmentEntry_IsNotWrittenOutsideTheRootAndIsReported()
    {
        var marker = $"dosai-nupkg-guard-{Guid.NewGuid():N}.cs";
        var escapeTarget = Path.Combine(Path.GetTempPath(), marker);
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "hostile.nupkg");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "lib/net8.0/Normal.cs", PayloadSource);
            WriteEntry(archive, "../" + marker, PayloadSource);
        }
        try
        {
            var slice = Extract(package);

            Assert.False(File.Exists(escapeTarget), $"entry '../{marker}' was extracted outside the extraction root");
            Assert.Single(slice.Diagnostics!, diagnostic => diagnostic.Contains(marker, StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(escapeTarget);
        }
    }

    [Fact]
    public void NupkgExtraction_BackslashParentSegmentEntry_IsSkippedAndReported()
    {
        var marker = $"dosai-nupkg-guard-{Guid.NewGuid():N}";
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "hostile.nupkg");
        var entryName = @"..\" + marker + ".dll";
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "lib/net8.0/Normal.cs", PayloadSource);
            WriteEntry(archive, entryName, "not an assembly");
        }

        var slice = Extract(package);

        Assert.Single(slice.Diagnostics!, diagnostic => diagnostic.Contains(entryName, StringComparison.Ordinal));
    }

    [Fact]
    public void NupkgExtraction_AbsoluteEntryNames_AreSkippedReportedAndNotWritten()
    {
        var absoluteMarker = $"dosai-nupkg-guard-{Guid.NewGuid():N}.cs";
        var driveMarker = $"dosai-nupkg-guard-{Guid.NewGuid():N}.cs";
        var absoluteEntry = "/tmp/" + absoluteMarker;
        var driveQualifiedEntry = @"C:\tmp\" + driveMarker;
        var absoluteTarget = "/tmp/" + absoluteMarker;
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "hostile.nupkg");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "lib/net8.0/Normal.cs", PayloadSource);
            WriteEntry(archive, absoluteEntry, PayloadSource);
            WriteEntry(archive, driveQualifiedEntry, PayloadSource);
        }
        try
        {
            var slice = Extract(package);

            Assert.False(File.Exists(absoluteTarget), $"entry '{absoluteEntry}' was extracted to its absolute path");
            Assert.Single(slice.Diagnostics!, diagnostic => diagnostic.Contains(absoluteEntry, StringComparison.Ordinal));
            Assert.Single(slice.Diagnostics!, diagnostic => diagnostic.Contains(driveQualifiedEntry, StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(absoluteTarget);
        }
    }

    [Fact]
    public void NupkgExtraction_DoubleDotInsideAFileName_IsNotAParentSegment()
    {
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "dots.nupkg");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "lib/net8.0/Guard..Pkg.cs", PayloadSource);
        }

        var slice = Extract(package);

        Assert.Contains(slice.Methods!, method => method.Name == "Add");
        Assert.DoesNotContain(slice.Diagnostics!, diagnostic => diagnostic.Contains("Guard..Pkg.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void NupkgExtraction_NormalEntries_StillExtractAndAreAnalyzed()
    {
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "wellformed.nupkg");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            // A directory entry plus a nested file: extraction must create the parent
            // directories and the analysis must see the nested source file.
            archive.CreateEntry("lib/");
            WriteEntry(archive, "lib/net8.0/C.cs", PayloadSource);
        }

        var slice = Extract(package);

        Assert.Contains(slice.Methods!, method => method.Name == "Add");
        Assert.Contains(slice.Methods!, method => method.Path?.Replace('\\', '/') == "lib/net8.0/C.cs");
        Assert.DoesNotContain(slice.Diagnostics ?? [], diagnostic => diagnostic.Contains("skipped", StringComparison.Ordinal));
    }

    private static MethodsSlice Extract(string package)
    {
        var json = Depscan.Dosai.GetMethodsFromNupkg(package);
        return JsonSerializer.Deserialize<MethodsSlice>(json, SliceOptions)!;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-nupkg-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
