using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Debug logging (--debug / DOSAI_DEBUG): phase lines on stderr, stdout and the JSON output
// untouched, heartbeat and thread-safety of the DebugLog helper itself. These tests share the
// DosaiTests collection (partial class) because they redirect the process-wide Console and
// flip the static DebugLog flag; part of that class runs the CLI in parallel otherwise.
public partial class DosaiTests
{
    private static readonly Regex DebugLinePrefix = new(@"^\[dosai \+\d+\.\d{3}s\] ", RegexOptions.Compiled);

    /// <summary>A stderr capture that is safe across the heartbeat timer thread and the test thread.</summary>
    private sealed class LineRecorder : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding { get; } = Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _lines.Add(value ?? string.Empty);
            }
        }

        public List<string> Snapshot()
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Copies the pre-existing Dosai.TestData fixtures (shipped next to the test assembly) into a fresh directory.</summary>
    private static string CopySmallFixture(string directory)
    {
        foreach (var fixture in new[] { "FooBar.cs", "HelloWorld.cs", "FooBar.vb", "HelloWorld.vb" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, fixture);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(directory, fixture));
            }
        }

        return directory;
    }

    /// <summary>
    ///     The methods JSON is byte-identical between a --debug and a plain run except for the
    ///     pre-existing GeneratedAt timestamp, which differs between any two runs regardless of
    ///     the flag (it is DateTimeOffset.UtcNow at serialization time), so both files are
    ///     normalized on that one property before comparing.
    /// </summary>
    private static string NormalizeGeneratedAt(string json) =>
        Regex.Replace(json, "\"GeneratedAt\":\"[^\"]+\"", "\"GeneratedAt\":\"<masked>\"");

    private static (int ExitCode, string Stderr) RunMethodsWithStderrCaptured(string path, string outputFile, bool debug)
    {
        // Isolate from a DOSAI_DEBUG set in the invoking environment: the no-debug half of the
        // comparison must not inherit it.
        var previous = Environment.GetEnvironmentVariable("DOSAI_DEBUG");
        Environment.SetEnvironmentVariable("DOSAI_DEBUG", null);
        var recorder = new LineRecorder();
        try
        {
            lock (ConsoleOutputLock)
            {
                var originalError = Console.Error;
                var originalOut = Console.Out;
                try
                {
                    Console.SetError(recorder);
                    // methods prints nothing to stdout, but keep it away from the test log anyway
                    Console.SetOut(TextWriter.Null);
                    var arguments = new List<string> { "methods", "--path", path, "--o", outputFile };
                    if (debug)
                    {
                        arguments.Add("--debug");
                    }
                    var exitCode = CommandLine.Main([.. arguments]);
                    return (exitCode, string.Join(Environment.NewLine, recorder.Snapshot()));
                }
                finally
                {
                    Console.SetError(originalError);
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOSAI_DEBUG", previous);
        }
    }

    [Fact]
    public void Methods_WithDebug_WritesPhaseLinesToStderr_AndLeavesJsonIdenticalToPlainRun()
    {
        using var tempDirectory = new TemporaryDirectory();
        var fixturePath = CopySmallFixture(tempDirectory.Path);
        var plainOutput = Path.Combine(tempDirectory.Path, "plain.json");
        var debugOutput = Path.Combine(tempDirectory.Path, "debug.json");

        var plain = RunMethodsWithStderrCaptured(fixturePath, plainOutput, debug: false);
        var debug = RunMethodsWithStderrCaptured(fixturePath, debugOutput, debug: true);

        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(0, debug.ExitCode);

        // Phase lines appear on stderr, in the documented shape.
        var debugLines = debug.Stderr.Split(Environment.NewLine);
        Assert.Contains(debugLines, line => DebugLinePrefix.IsMatch(line) && line.EndsWith(" start methods", StringComparison.Ordinal));
        Assert.Contains(debugLines, line => DebugLinePrefix.IsMatch(line) && line.Contains("end methods in ", StringComparison.Ordinal) && line.Contains("managed heap ", StringComparison.Ordinal) && line.Contains("working set ", StringComparison.Ordinal));
        Assert.Contains(debugLines, line => DebugLinePrefix.IsMatch(line) && line.Contains("start methods.assembly-inspection", StringComparison.Ordinal));

        // Debug stays off stderr without the flag.
        Assert.DoesNotContain("[dosai", plain.Stderr, StringComparison.Ordinal);

        // The analysis output itself is untouched by the flag.
        Assert.Equal(NormalizeGeneratedAt(File.ReadAllText(plainOutput)), NormalizeGeneratedAt(File.ReadAllText(debugOutput)));
    }

    [Fact]
    public void Methods_WithoutDebugAndWithoutDosaiDebug_WritesNoDebugLinesToStderr()
    {
        using var tempDirectory = new TemporaryDirectory();
        var fixturePath = CopySmallFixture(tempDirectory.Path);
        var output = Path.Combine(tempDirectory.Path, "out.json");

        var run = RunMethodsWithStderrCaptured(fixturePath, output, debug: false);

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[dosai", run.Stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void DosaiDebugEnvironmentVariable_EnablesDebugLogging()
    {
        using var tempDirectory = new TemporaryDirectory();
        var fixturePath = CopySmallFixture(tempDirectory.Path);
        var output = Path.Combine(tempDirectory.Path, "out.json");
        var recorder = new LineRecorder();
        var previous = Environment.GetEnvironmentVariable("DOSAI_DEBUG");

        try
        {
            Environment.SetEnvironmentVariable("DOSAI_DEBUG", "1");
            lock (ConsoleOutputLock)
            {
                var originalError = Console.Error;
                try
                {
                    Console.SetError(recorder);
                    Assert.Equal(0, CommandLine.Main(["methods", "--path", fixturePath, "--o", output]));
                }
                finally
                {
                    Console.SetError(originalError);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOSAI_DEBUG", previous);
        }

        var lines = recorder.Snapshot();
        Assert.Contains(lines, line => line.Contains("[dosai +", StringComparison.Ordinal) && line.Contains("start methods", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DosaiDebugEnvironmentVariable_ParsingAcceptsOneAndTrueOnly(string? value, bool expected)
    {
        Assert.Equal(expected, DebugLog.IsTruthyEnvironmentValue(value));
    }

    [Fact]
    public void Mcp_WithDebug_StdoutRemainsValidJsonRpc()
    {
        using var tempDirectory = new TemporaryDirectory();
        CopySmallFixture(tempDirectory.Path);
        var recorder = new LineRecorder();
        var stdout = new StringWriter();
        var stdin = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}" + Environment.NewLine);

        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            var originalIn = Console.In;
            var originalError = Console.Error;
            try
            {
                Console.SetOut(stdout);
                Console.SetIn(stdin);
                Console.SetError(recorder);
                var exitCode = CommandLine.Main(["mcp", "--path", tempDirectory.Path, "--debug"]);
                Assert.Equal(0, exitCode);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetIn(originalIn);
                Console.SetError(originalError);
            }
        }

        // Debug lines went to stderr only; stdout carries exactly one parseable JSON-RPC response.
        var stdoutText = stdout.ToString();
        Assert.DoesNotContain("[dosai", stdoutText, StringComparison.Ordinal);
        Assert.Contains(recorder.Snapshot(), line => line.Contains("[dosai +", StringComparison.Ordinal));

        var response = Assert.Single(stdoutText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        using var document = JsonDocument.Parse(response);
        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("dosai", document.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void DebugLog_PhaseAndLog_AreSafeFromSeveralThreadsAtOnce()
    {
        var previousInterval = DebugLog.HeartbeatInterval;
        var recorder = new LineRecorder();
        try
        {
            DebugLog.HeartbeatInterval = TimeSpan.FromMinutes(5); // keep the heartbeat out of this test
            lock (ConsoleOutputLock)
            {
                var originalError = Console.Error;
                try
                {
                    Console.SetError(recorder);
                    DebugLog.Configure(true);

                    // CountdownEvent instead of Task.WaitAll: xUnit's analyzer rejects blocking
                    // task operations, and the countdown keeps the whole test inside the console
                    // lock (the await alternative cannot).
                    const int workerCount = 8;
                    const int iterations = 50;
                    using var done = new CountdownEvent(workerCount);
                    var tasks = new Task[workerCount];
                    for (var worker = 0; worker < workerCount; worker++)
                    {
                        var workerName = $"worker-{worker}";
                        tasks[worker] = Task.Run(() =>
                        {
                            try
                            {
                                for (var iteration = 0; iteration < iterations; iteration++)
                                {
                                    using var phase = DebugLog.Phase($"{workerName}-phase-{iteration}");
                                    DebugLog.Log($"{workerName} message {iteration}");
                                    DebugLog.Count($"{workerName} count", iteration);
                                }
                            }
                            finally
                            {
                                done.Signal();
                            }
                        });
                    }

                    Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "every worker must finish");
                    Assert.All(tasks, task => Assert.True(task.IsCompletedSuccessfully, task.Exception?.ToString()));

                    DebugLog.Configure(false);
                }
                finally
                {
                    Console.SetError(originalError);
                }
            }
        }
        finally
        {
            DebugLog.Configure(false);
            DebugLog.HeartbeatInterval = previousInterval;
        }

        var lines = recorder.Snapshot();
        // Other test classes run analyzers in parallel and their debug lines can land in the
        // same global stderr while this test holds Enabled=true, so every assertion matches
        // only this test's own markers (worker-N-phase / worker-N message / worker-N count).
        var ownLines = lines.Where(line => line.Contains("worker-", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(ownLines);
        foreach (var line in ownLines)
        {
            Assert.StartsWith("[dosai +", line, StringComparison.Ordinal);
            Assert.Matches(@"^\[dosai \+\d+\.\d{3}s\] ", line);
        }

        Assert.Equal(400, ownLines.Count(line => Regex.IsMatch(line, @"start worker-\d+-phase-")));
        Assert.Equal(400, ownLines.Count(line => Regex.IsMatch(line, @"end worker-\d+-phase-")));
        Assert.Equal(400, ownLines.Count(line => Regex.IsMatch(line, @"worker-\d+ message \d+$")));
        Assert.Equal(400, ownLines.Count(line => Regex.IsMatch(line, @"worker-\d+ count: \d+$")));

        // Every line, foreign ones included, is intact and from one clock: timestamps never
        // go backwards even under concurrency.
        var previousElapsed = -1m;
        foreach (var elapsed in lines.Select(line => decimal.Parse(line.Split('+')[1].Split('s')[0], System.Globalization.CultureInfo.InvariantCulture)))
        {
            Assert.True(elapsed >= previousElapsed, $"timestamps must not go backwards: {elapsed} after {previousElapsed}");
            previousElapsed = elapsed;
        }
    }

    [Fact]
    public void DebugLog_Heartbeat_FiresWhileAPhaseRunsAndStopsAfterDisposal()
    {
        var previousInterval = DebugLog.HeartbeatInterval;
        var recorder = new LineRecorder();
        try
        {
            DebugLog.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
            lock (ConsoleOutputLock)
            {
                var originalError = Console.Error;
                try
                {
                    Console.SetError(recorder);
                    DebugLog.Configure(true);
                    using (var phase = DebugLog.Phase("slow-phase"))
                    {
                        // The phase outlives the injected interval, so the heartbeat must fire.
                        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                        while (DateTime.UtcNow < deadline && recorder.Snapshot().All(line => !line.Contains("still in slow-phase", StringComparison.Ordinal)))
                        {
                            Thread.Sleep(20);
                        }

                        Assert.Contains(recorder.Snapshot(), line => DebugLinePrefix.IsMatch(line) && line.Contains("still in slow-phase", StringComparison.Ordinal) && line.Contains("managed heap ", StringComparison.Ordinal));
                    }

                    // Disposal stops the heartbeat: no further slow-phase lines after the timer
                    // is gone (other test classes may contribute unrelated stderr lines while
                    // this test holds Enabled=true, so filter to this phase's own lines).
                    var settled = recorder.Snapshot().Count(line => line.Contains("still in slow-phase", StringComparison.Ordinal));
                    Thread.Sleep(250);
                    Assert.Equal(settled, recorder.Snapshot().Count(line => line.Contains("still in slow-phase", StringComparison.Ordinal)));
                    DebugLog.Configure(false);
                }
                finally
                {
                    Console.SetError(originalError);
                }
            }
        }
        finally
        {
            DebugLog.Configure(false);
            DebugLog.HeartbeatInterval = previousInterval;
        }
    }

    [Fact]
    public void DebugLog_Disabled_LogAndPhaseCostNothingAndWriteNothing()
    {
        var recorder = new LineRecorder();
        lock (ConsoleOutputLock)
        {
            var originalError = Console.Error;
            try
            {
                Console.SetError(recorder);
                DebugLog.Configure(false);
                DebugLog.Log("must not appear");
                DebugLog.Count("must-not-appear", 42);
                DebugLog.Log(() => throw new InvalidOperationException("lazy message must not be evaluated"));
                using (DebugLog.Phase("must-not-appear"))
                {
                }

                Assert.Empty(recorder.Snapshot());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }
}
