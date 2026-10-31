namespace Depscan;

/// <summary>
///     How much of the dependency assemblies in a built tree the data-flow IL pass analyzes
///     (issue #107). A build output carries every package whole, and interpreting all of it cost
///     over a minute and 140 MB of output on a minimal API with MailKit.
///     <para>
///         <see cref="All" /> (the default for a built source tree, the policy of issue #78)
///         analyzes every method of every dependency assembly, so flows that stay inside package code are in the graph, scoped
///         <c>dependency</c> and capped at low severity. <see cref="Reachable" /> analyzes a
///         dependency method only when application code reaches it by a chain of calls, so the
///         flows from the application into a package, and through it, are kept (the application
///         slices are identical in both modes) while flows nothing in the application can start
///         are left out. It is the default for an assembly-only input (see <see cref="For" />).
///     </para>
///     Applied per command with <see cref="Apply" />, like <see cref="PathExclusions" />, or
///     process-wide by <c>DOSAI_DEPENDENCY_FLOWS</c>.
/// </summary>
public enum DependencyFlowMode
{
    All,
    Reachable
}

public static class DependencyFlowScope
{
    private static readonly AsyncLocal<DependencyFlowMode?> CurrentScope = new();

    /// <summary>The mode asked for: the applied one, else the <c>DOSAI_DEPENDENCY_FLOWS</c> environment variable, else null.</summary>
    public static DependencyFlowMode? Requested => CurrentScope.Value
        ?? (TryParse(Environment.GetEnvironmentVariable("DOSAI_DEPENDENCY_FLOWS"), out var fromEnvironment) ? fromEnvironment : null);

    /// <summary>
    ///     The mode in effect for a scan: the requested one, else <see cref="DependencyFlowMode.Reachable" />
    ///     for an assembly-only input (a publish directory or a container file system, where the
    ///     packages are most of the files and no source names the application's own code) and
    ///     <see cref="DependencyFlowMode.All" /> for a built source tree, the policy of issue #78.
    /// </summary>
    public static DependencyFlowMode For(bool assemblyOnlyInput) =>
        Requested ?? (assemblyOnlyInput ? DependencyFlowMode.Reachable : DependencyFlowMode.All);

    public static bool TryParse(string? value, out DependencyFlowMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "all":
                mode = DependencyFlowMode.All;
                return true;
            case "reachable":
                mode = DependencyFlowMode.Reachable;
                return true;
            default:
                mode = DependencyFlowMode.All;
                return false;
        }
    }

    /// <summary>Uses <paramref name="mode" /> (when not null) until the returned scope is disposed.</summary>
    public static IDisposable Apply(DependencyFlowMode? mode)
    {
        var previous = CurrentScope.Value;
        if (mode is not null)
        {
            CurrentScope.Value = mode;
        }

        return new Scope(previous);
    }

    private sealed class Scope(DependencyFlowMode? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}
