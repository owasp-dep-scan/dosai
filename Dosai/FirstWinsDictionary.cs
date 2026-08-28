namespace Depscan;

internal static class FirstWinsDictionary
{
    // Graph node/edge/evidence IDs are expected to be unique, but real-world scans can
    // surface the same member from more than one source (e.g. a DLL copied into two
    // scanned folders, or combined source+assembly evidence). First wins instead of
    // throwing ArgumentException from ToDictionary on duplicate keys.
    public static Dictionary<string, T> ToDictionaryFirstWins<T>(this IEnumerable<T> source, Func<T, string> keySelector, StringComparer comparer)
        => source.GroupBy(keySelector, comparer).ToDictionary(group => group.Key, group => group.First(), comparer);
}
