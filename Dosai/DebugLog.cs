using System.Diagnostics;

namespace Depscan;

/// <summary>
///     Opt-in progress log for long analyses, written to stderr so stdout is never corrupted:
///     JSON output files and the MCP server's JSON-RPC stream stay clean. Enabled once from the
///     CLI (<c>--debug</c> or <c>DOSAI_DEBUG=1|true</c>); a static flag keeps every analyzer
///     callable without threading a logger through signatures.
///     <para>
///         When disabled every method returns immediately and costs one flag check - callers
///         guard message construction behind <see cref="Enabled" /> so strings in hot loops are
///         never built. When enabled, output is phase granularity only (never per analyzed
///         item): <c>[dosai +12.345s] start &lt;phase&gt;</c>, <c>end &lt;phase&gt; in 1.234s,
///         managed heap 812 MB, working set 1.9 GB</c>, and a heartbeat naming the innermost
///         running phase for runs that outlast the interval. All writes go through one lock, so
///         lines stay intact from any thread - including the dedicated large-stack assembly
///         inspection thread - and the single elapsed clock keeps timestamps monotonic.
///     </para>
///     <para>
///         Log only paths, counts, assembly and phase names, and timings. Never file contents,
///         source text, string literals, secret values, environment variable values, or child
///         process command lines.
///     </para>
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();

    /// <summary>Single monotonic clock; started at first use, which is CLI startup, so elapsed reads as time since process start.</summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Currently open phases in start order; the last entry is the innermost ("current") phase the heartbeat names.</summary>
    private static readonly List<PhaseScope> ActivePhases = [];

    private static Timer? heartbeat;
    private static int heartbeatGeneration;

    public static bool Enabled { get; private set; }

    private static long excludedDirectories;
    private static long excludedFiles;

    /// <summary>
    ///     Counts a directory pruned or a file skipped by the active <c>--exclude</c> scope. Called
    ///     from the shared tree walk itself, so the totals describe what the analyzers skipped.
    /// </summary>
    internal static void NoteExcluded(bool isDirectory)
    {
        if (!Enabled)
        {
            return;
        }

        if (isDirectory)
        {
            Interlocked.Increment(ref excludedDirectories);
        }
        else
        {
            Interlocked.Increment(ref excludedFiles);
        }
    }

    /// <summary>Running totals of <see cref="NoteExcluded" /> for the current process.</summary>
    internal static (long Directories, long Files) ExcludedTotals
        => (Interlocked.Read(ref excludedDirectories), Interlocked.Read(ref excludedFiles));

    /// <summary>Heartbeat cadence; injectable so tests do not wait 30 seconds.</summary>
    internal static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><c>DOSAI_DEBUG</c> turns the log on for the values <c>1</c> and <c>true</c> (case-insensitive, whitespace-tolerant); anything else leaves it off.</summary>
    public static bool IsTruthyEnvironmentValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Set once per CLI invocation (flag or environment); also how in-process tests disable a previously enabled log.</summary>
    public static void Configure(bool enabled)
    {
        Enabled = enabled;
        Interlocked.Exchange(ref excludedDirectories, 0);
        Interlocked.Exchange(ref excludedFiles, 0);
    }

    /// <summary>Writes <c>[dosai +12.345s] message</c> to stderr. No-op unless enabled.</summary>
    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            Console.Error.WriteLine($"[dosai +{Clock.Elapsed.TotalSeconds:F3}s] {message}");
        }
    }

    /// <summary>Lazy-string overload for messages whose construction is not free; the factory only runs when enabled.</summary>
    public static void Log(Func<string> message)
    {
        if (Enabled)
        {
            Log(message());
        }
    }

    /// <summary>Logs a size or count, e.g. <c>call graph nodes: 1234</c>. No-op unless enabled.</summary>
    public static void Count(string name, long value)
    {
        if (Enabled)
        {
            Log($"{name}: {value}");
        }
    }

    /// <summary>
    ///     Opens a phase: logs <c>start &lt;name&gt;</c> and returns a scope that logs
    ///     <c>end &lt;name&gt; in 1.234s, managed heap 812 MB, working set 1.9 GB</c> on dispose.
    ///     While any phase is open, a background timer reports the innermost phase every
        ///     <see cref="HeartbeatInterval" />; it is disposed when the last phase closes and so
    ///     cannot keep the process alive or fire after command completion. When disabled,
    ///     returns a shared no-op scope.
    /// </summary>
    public static IDisposable Phase(string name)
    {
        if (!Enabled)
        {
            return NoopPhase.Instance;
        }

        var scope = new PhaseScope(name);
        lock (Gate)
        {
            ActivePhases.Add(scope);
            Console.Error.WriteLine($"[dosai +{Clock.Elapsed.TotalSeconds:F3}s] start {name}");
            StartHeartbeatLocked();
        }

        return scope;
    }

    /// <summary>Runs <paramref name="work" /> inside <see cref="Phase" /> and returns its result.</summary>
    public static T Measure<T>(string name, Func<T> work)
    {
        using var phase = Phase(name);
        return work();
    }

    /// <summary>Runs <paramref name="work" /> inside <see cref="Phase" />.</summary>
    public static void Measure(string name, Action work)
    {
        using var phase = Phase(name);
        work();
    }

    private static void StartHeartbeatLocked()
    {
        // One timer for the process, started by the first phase; nested phases reuse it, the
        // tick always names the innermost open phase.
        if (ActivePhases.Count > 1 || heartbeat is not null)
        {
            return;
        }

        heartbeatGeneration++;
        heartbeat = new Timer(HeartbeatTick, heartbeatGeneration, HeartbeatInterval, HeartbeatInterval);
    }

    private static void StopHeartbeatIfIdleLocked()
    {
        if (ActivePhases.Count > 0 || heartbeat is null)
        {
            return;
        }

        heartbeatGeneration++;
        heartbeat.Dispose();
        heartbeat = null;
    }

    private static void HeartbeatTick(object? state)
    {
        lock (Gate)
        {
            // A tick racing the disposal of its own timer (generation moved on) must be a no-op.
            if (heartbeat is null || state is not int generation || generation != heartbeatGeneration || ActivePhases.Count == 0)
            {
                return;
            }

            Console.Error.WriteLine($"[dosai +{Clock.Elapsed.TotalSeconds:F3}s] still in {ActivePhases[^1].Name}, managed heap {FormatBytes(GC.GetTotalMemory(forceFullCollection: false))}, working set {FormatBytes(Environment.WorkingSet)}");
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / (double)1_073_741_824:F1} GB",
        >= 1_048_576 => $"{bytes / (double)1_048_576:F0} MB",
        >= 1024 => $"{bytes / (double)1024:F0} KB",
        _ => $"{bytes} B"
    };

    private sealed class NoopPhase : IDisposable
    {
        public static readonly NoopPhase Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class PhaseScope(string name) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _disposed;

        public string Name { get; } = name;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ActivePhases.Remove(this);
                Console.Error.WriteLine($"[dosai +{Clock.Elapsed.TotalSeconds:F3}s] end {Name} in {_watch.Elapsed.TotalSeconds:F3}s, managed heap {FormatBytes(GC.GetTotalMemory(forceFullCollection: false))}, working set {FormatBytes(Environment.WorkingSet)}");
                StopHeartbeatIfIdleLocked();
            }
        }
    }
}
