using System.Runtime.ExceptionServices;

namespace Depscan;

/// <summary>
///     Runs analysis work on a thread with a reserved-large stack. Two unbounded recursions live
///     under analysis and neither can be bounded from Dosai code: the runtime type loader resolving
///     a type's base chain (dotnet/runtime#131679, issue #58) and the Roslyn operation factory
///     descending roughly one frame set per call in a fluent chain (issue #60). .NET terminates the
///     process on a stack overflow rather than raising a catchable exception, so a caller cannot
///     recover and no partial output is saved; the only remedy is a stack large enough that real
///     code cannot exhaust it. Every Roslyn- or reflection-driven entry point therefore routes its
///     work through <see cref="Run{T}(string, Func{T})" />, and nested calls (an analysis phase
///     invoking another guarded entry point) simply reserve one more stack region, which costs
///     address space only while the inner thread runs.
/// </summary>
internal static class DedicatedStack
{
    /// <summary>
    ///     Stack reserved per analysis thread. Reserved address space is committed only as it is
    ///     used, so a large reservation costs nothing on the common, shallow path.
    /// </summary>
    internal static readonly int AnalysisStackSize = Environment.Is64BitProcess ? 256 * 1024 * 1024 : 64 * 1024 * 1024;

    /// <summary>
    ///     Run <paramref name="work" /> on a dedicated thread with <see cref="AnalysisStackSize" />
    ///     of stack and return its result; exceptions propagate to the caller unchanged.
    /// </summary>
    internal static T Run<T>(string threadName, Func<T> work)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;
        var worker = new Thread(() =>
        {
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
            Name = threadName
        };
        worker.Start();
        worker.Join();
        failure?.Throw();
        return result!;
    }
}
