using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Same xunit collection as the rest of DosaiTests: this flips the static worker knob.
public partial class DosaiTests
{
    [Fact]
    public void LanguageFrontends_AreIdenticalForEveryWorkerCount()
    {
        // Files of every frontend, many of each, so the team interleaves languages; the merged
        // lists must keep file order exactly.
        using var tempDirectory = new TemporaryDirectory();
        var dataDirectory = Directory.GetCurrentDirectory();
        for (var copy = 0; copy < 12; copy++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, $"m{copy:D2}")).FullName;
            foreach (var source in Directory.EnumerateFiles(dataDirectory, "*.fs").Concat(Directory.EnumerateFiles(dataDirectory, "*.R")))
            {
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            }

            File.WriteAllText(Path.Combine(directory, "native.cpp"), $$"""
                #include <vector>
                #include "local{{copy}}.h"
                namespace ns{{copy}} {
                class Widget {
                public:
                    int Size() const { return Helper{{copy}}(items.size()); }
                };
                static int Helper{{copy}}(int value) { return printf("%d", value); }
                }
                """);
            File.WriteAllText(Path.Combine(directory, $"local{copy}.h"), $"int Helper{copy}(int value);\nvoid Shared(void) {{ Helper{copy}(1); }}\n");
        }

        string Run(int workers) => WithSymbolAnalysisWorkers(workers, () =>
        {
            var (methods, dependencies, calls) = LanguageFrontendAnalyzer.GetMethods(tempDirectory.Path);
            Assert.NotEmpty(methods);
            Assert.NotEmpty(calls);
            return JsonSerializer.Serialize(new { methods, dependencies, calls }, JsonStringEnums);
        });

        var sequential = Run(1);
        Assert.Equal(sequential, Run(4));
        Assert.Equal(sequential, Run(16));
    }
}
