using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Depscan;

/// <summary>
///     Runs analysis work on a thread with a reserved-large stack. Two unbounded recursions live
///     under analysis and neither can be bounded from Dosai code: the runtime type loader resolving
///     a type's base chain (dotnet/runtime#131679, issue #58) and the Roslyn operation factory
///     descending roughly one frame set per call in a fluent chain (issue #60). .NET terminates the
///     process on a stack overflow rather than raising a catchable exception, so a caller cannot
///     recover and no partial output is saved; the remedy is a stack large enough that real code
///     cannot exhaust it, with <see cref="OperationDepthGuard" /> keeping pathological members off
///     the operation factory altogether. Every Roslyn- or reflection-driven entry point therefore
///     routes its work through <see cref="Run{T}(string, Func{T})" />. A nested call (crypto
///     analysis running the methods and data-flow pipelines, methods analysis inspecting
///     assemblies) is already on such a thread and runs inline instead of reserving another stack.
/// </summary>
internal static class DedicatedStack
{
    /// <summary>
    ///     Stack reserved per analysis thread. Reserved address space is committed only as it is
    ///     used, so a large reservation costs nothing on the common, shallow path.
    /// </summary>
    internal static readonly int AnalysisStackSize = Environment.Is64BitProcess ? 256 * 1024 * 1024 : 64 * 1024 * 1024;

    [ThreadStatic]
    private static bool onAnalysisThread;

    /// <summary>True on a thread started by <see cref="Run{T}(string, Func{T})" />.</summary>
    internal static bool IsOnAnalysisThread => onAnalysisThread;

    /// <summary>
    ///     Run <paramref name="work" /> on a dedicated thread with <see cref="AnalysisStackSize" />
    ///     of stack and return its result; exceptions propagate to the caller unchanged. The
    ///     worker carries the caller's culture and, through the captured execution context, its
    ///     ambient scopes such as <see cref="PathExclusions" />.
    /// </summary>
    internal static T Run<T>(string threadName, Func<T> work)
    {
        if (onAnalysisThread)
        {
            return work();
        }

        T? result = default;
        ExceptionDispatchInfo? failure = null;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var worker = new Thread(() =>
        {
            onAnalysisThread = true;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }, AnalysisStackSize)
        {
            Name = threadName,
            // The caller always joins; a background worker can never hold the process open.
            IsBackground = true
        };
        try
        {
            worker.Start();
        }
        catch (OutOfMemoryException)
        {
            // No address space or commit for the reservation (a 32-bit host, a strict-overcommit
            // container): analyze on the caller's stack, which ordinary code never exhausts,
            // rather than fail every run.
            return work();
        }

        worker.Join();
        failure?.Throw();
        return result!;
    }

    /// <summary>
    ///     Run <paramref name="workerCount" /> independent partitions, each on its own thread with
    ///     <see cref="AnalysisStackSize" /> of stack, and return when every partition has finished
    ///     (issue #65: the per-file symbol-analysis loop). Every worker needs the large stack, not
    ///     just the coordinating thread - <see cref="OperationDepthGuard" /> sizes its depth budget
    ///     from the thread it runs on, so a thread-pool worker would both skip members the
    ///     dedicated thread analyzes and lose the overflow protection. Workers carry the caller's
    ///     culture and ambient scopes; the first failure by worker index is rethrown after all
    ///     workers have joined, so a failing scan still reports one deterministic exception.
    /// </summary>
    /// <param name="workerBody">Receives the worker index in <c>[0, workerCount)</c>.</param>
    internal static void RunPartitions(string threadName, int workerCount, Action<int> workerBody)
    {
        if (workerCount <= 1)
        {
            workerBody(0);
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var failures = new ExceptionDispatchInfo?[workerCount];
        var workers = new Thread[workerCount];
        var started = new bool[workerCount];
        for (var index = 0; index < workerCount; index++)
        {
            var partition = index;
            workers[index] = new Thread(() =>
            {
                onAnalysisThread = true;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                try
                {
                    workerBody(partition);
                }
                catch (Exception e)
                {
                    failures[partition] = ExceptionDispatchInfo.Capture(e);
                }
            }, AnalysisStackSize)
            {
                Name = $"{threadName} #{partition + 1}",
                IsBackground = true
            };
        }

        for (var index = 0; index < workerCount; index++)
        {
            try
            {
                workers[index].Start();
                started[index] = true;
            }
            catch (OutOfMemoryException)
            {
                // Same fallback as Run: without a reservation the partition runs inline. The
                // coordinator is normally an analysis thread; on a small caller stack the
                // OperationDepthGuard budget tightens and skips members instead of crashing.
                started[index] = false;
            }
        }

        for (var index = 0; index < workerCount; index++)
        {
            if (started[index])
            {
                workers[index].Join();
            }
            else
            {
                workerBody(index);
            }
        }

        foreach (var failure in failures)
        {
            failure?.Throw();
        }
    }
}
