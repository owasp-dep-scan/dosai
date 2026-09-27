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
    ///     worker carries the caller's culture (member modifiers are title-cased through it) and,
    ///     through the captured execution context, its ambient scopes such as
    ///     <see cref="PathExclusions" />.
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
}
