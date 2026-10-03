using System.Collections.Concurrent;
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
    ///     <see cref="ForEach" /> for a producer whose results must be consumed in index order:
    ///     each worker produces item <c>i</c> on its own, and the consumer then runs under one
    ///     lock for every finished item at the head of the order, so it sees items exactly as a
    ///     sequential loop would hand them over. Producers run at most a window of items ahead of
    ///     the consumer, which bounds the results waiting in memory when one item is slow. A
    ///     failure stops the workers waiting on the window and is rethrown like
    ///     <see cref="ForEach" />'s.
    /// </summary>
    internal static void ForEachInOrder<T>(string threadName, int workerCount, int itemCount, Func<int, T> produce, Action<T> consume) where T : class
    {
        var window = Math.Max(1, workerCount) * 2;
        var results = new T?[Math.Max(0, itemCount)];
        var gate = new object();
        var next = 0;
        var failed = false;
        ForEach(threadName, workerCount, itemCount, index =>
        {
            lock (gate)
            {
                while (!failed && index >= next + window)
                {
                    Monitor.Wait(gate);
                }

                if (failed)
                {
                    return;
                }
            }

            T result;
            try
            {
                result = produce(index);
            }
            catch
            {
                lock (gate)
                {
                    failed = true;
                    Monitor.PulseAll(gate);
                }

                throw;
            }

            lock (gate)
            {
                results[index] = result;
                try
                {
                    while (!failed && next < results.Length && results[next] is { } ready)
                    {
                        results[next] = null;
                        next++;
                        consume(ready);
                    }
                }
                catch
                {
                    failed = true;
                    throw;
                }
                finally
                {
                    Monitor.PulseAll(gate);
                }
            }
        });
    }

    /// <summary>
    ///     Run <paramref name="body" /> for every index in <c>[0, itemCount)</c> on up to
    ///     <paramref name="workerCount" /> threads, each with <see cref="AnalysisStackSize" /> of
    ///     stack, and return when all of them have finished (issue #65: the per-file symbol
    ///     analysis and the dispatch-index scan). Every worker needs the large stack, not just the
    ///     coordinating thread: <see cref="OperationDepthGuard" /> sizes its depth budget from the
    ///     thread it runs on, so a thread-pool worker would skip members the dedicated thread
    ///     analyzes and lose the overflow protection.
    ///     <para>
    ///         Workers claim the next unclaimed index as they free up, so one large file occupies
    ///         one worker while the others keep draining the rest. <paramref name="body" /> must
    ///         write only to its own index's slot. Failures keep sequential semantics: no index
    ///         past the lowest failing one is started, and that lowest failure is rethrown after
    ///         every worker has joined - the exception a sequential loop would have stopped on.
    ///         Workers carry the caller's culture and, through the captured execution context,
    ///         its ambient scopes such as <see cref="PathExclusions" />.
    ///     </para>
    /// </summary>
    internal static void ForEach(string threadName, int workerCount, int itemCount, Action<int> body)
    {
        if (itemCount <= 0)
        {
            return;
        }

        workerCount = Math.Min(workerCount, itemCount);
        if (workerCount <= 1)
        {
            for (var index = 0; index < itemCount; index++)
            {
                body(index);
            }

            return;
        }

        var nextIndex = -1;
        var lowestFailure = int.MaxValue;
        var failures = new ConcurrentDictionary<int, ExceptionDispatchInfo>();
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var workers = new List<Thread>(workerCount);
        for (var worker = 0; worker < workerCount; worker++)
        {
            var thread = new Thread(() =>
            {
                onAnalysisThread = true;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                Drain();
            }, AnalysisStackSize)
            {
                Name = $"{threadName} #{worker + 1}",
                // The caller always joins; a background worker can never hold the process open.
                IsBackground = true
            };
            try
            {
                thread.Start();
                workers.Add(thread);
            }
            catch (OutOfMemoryException)
            {
                // No address space or commit for another reservation: the workers already running
                // share the remaining indexes between them.
                break;
            }
        }

        if (workers.Count == 0)
        {
            // Same fallback as Run: without any reservation the loop runs on the caller's stack.
            Drain();
        }

        foreach (var thread in workers)
        {
            thread.Join();
        }

        if (lowestFailure != int.MaxValue)
        {
            failures[lowestFailure].Throw();
        }

        void Drain()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= itemCount || index > Volatile.Read(ref lowestFailure))
                {
                    return;
                }

                try
                {
                    body(index);
                }
                catch (Exception exception)
                {
                    failures[index] = ExceptionDispatchInfo.Capture(exception);
                    var observed = Volatile.Read(ref lowestFailure);
                    while (index < observed)
                    {
                        var replaced = Interlocked.CompareExchange(ref lowestFailure, index, observed);
                        if (replaced == observed)
                        {
                            break;
                        }

                        observed = replaced;
                    }
                }
            }
        }
    }
}
