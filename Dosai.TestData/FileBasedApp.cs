// A .NET 10/11 file-based app (`dotnet run app.cs`): the `#:` directives carry SDK properties,
// packages, and the SDK flavor, and the program is top-level statements plus trailing type
// declarations. The C# parser must accept the directives as trivia (they are only valid under
// the FileBasedProgram parser feature) instead of reporting CS9298 for every line, and the
// analysis must inventory the compiler-synthesized `<Main>$` entry point, the local function,
// and the trailing union - taint flows from the Console.ReadLine source to Process.Start in
// the entry point and to the .NET 11 run-and-capture process sinks in the helper.
//
// Source-mode fixture only: compiling it into Dosai.TestData.CSharp would change that
// assembly's member set and with it the method tables the assembly-mode tests expect.
#:property TieredPGO=false
#:property Nullable=enable
#:package Microsoft.Extensions.Logging@9.0.0
#:sdk Microsoft.NET.Sdk.Web

using System.Diagnostics;

var command = Console.ReadLine() ?? string.Empty;
Console.WriteLine(RunCaptured(command));
Helpers.Report(command);

static string RunCaptured(string command)
{
    var output = Process.RunAndCaptureText(command);
    Process.StartAndForget("logger", ["-m", command]);
    return output.StandardOutput;
}

public sealed record Cat(string Name);
public sealed record Dog(string Name);
public union Pet(Cat, Dog);

public static class Helpers
{
    public static void Report(string command) => Console.WriteLine($"ran {command}");
}
