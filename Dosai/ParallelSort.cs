namespace Depscan;

/// <summary>
///     A stable sort on the worker team: equal elements keep their input order, so the result is
///     exactly a sequential stable sort's for every worker count. The array is cut into one run
///     per worker; each run is sorted on its own (an index sort with an input-position tiebreak),
///     and adjacent runs are merged pairwise, left run first on ties, until one run is left. Each
///     merge is split into output ranges along its merge path (a binary search finds how many
///     elements of each run fill an output prefix), so every round, the last one included, keeps
///     the whole team busy. Graph edge lists run into the millions and were sorted on one thread.
/// </summary>
internal static class ParallelSort
{
    /// <summary>Below this many elements per worker the sort stays sequential.</summary>
    internal const int MinRunLength = 1 << 15;

    public static void StableSort<T>(T[] items, Comparison<T> compare) => StableSort(items, compare, Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), MinRunLength);

    internal static void StableSort<T>(T[] items, Comparison<T> compare, int workers, int minRunLength)
    {
        var count = items.Length;
        var runCount = Math.Max(1, Math.Min(workers, count / Math.Max(1, minRunLength)));
        if (runCount == 1)
        {
            SortRun(items, 0, count, compare);
            return;
        }

        var bounds = new int[runCount + 1];
        for (var run = 0; run <= runCount; run++)
        {
            bounds[run] = (int)((long)count * run / runCount);
        }

        DedicatedStack.ForEach("Dosai sort", runCount, runCount, run => SortRun(items, bounds[run], bounds[run + 1], compare));

        var source = items;
        var target = new T[count];
        while (bounds.Length > 2)
        {
            var runs = bounds.Length - 1;
            var pairs = (runs + 1) / 2;
            var merged = new int[pairs + 1];
            for (var pair = 0; pair < pairs; pair++)
            {
                merged[pair] = bounds[2 * pair];
            }

            merged[pairs] = count;
            // Output slices per pair, about one per worker across the round.
            var slices = Math.Max(1, workers / pairs);
            var currentSource = source;
            var currentTarget = target;
            var currentBounds = bounds;
            DedicatedStack.ForEach("Dosai sort", workers, pairs * slices, task =>
            {
                var pair = task / slices;
                var slice = task % slices;
                var leftStart = currentBounds[2 * pair];
                var leftEnd = currentBounds[Math.Min(2 * pair + 1, runs)];
                var rightEnd = currentBounds[Math.Min(2 * pair + 2, runs)];
                var length = rightEnd - leftStart;
                var from = (int)((long)length * slice / slices);
                var to = (int)((long)length * (slice + 1) / slices);
                MergeSlice(currentSource, leftStart, leftEnd - leftStart, leftEnd, rightEnd - leftEnd, currentTarget, from, to, compare);
            });

            (source, target) = (target, source);
            bounds = merged;
        }

        if (!ReferenceEquals(source, items))
        {
            Array.Copy(source, items, count);
        }
    }

    /// <summary>Stable sort of <c>items[start..end)</c> in place.</summary>
    private static void SortRun<T>(T[] items, int start, int end, Comparison<T> compare)
    {
        var length = end - start;
        if (length < 2)
        {
            return;
        }

        var order = new int[length];
        for (var index = 0; index < length; index++)
        {
            order[index] = start + index;
        }

        Array.Sort(order, (x, y) =>
        {
            var c = compare(items[x], items[y]);
            return c != 0 ? c : x.CompareTo(y);
        });
        var sorted = new T[length];
        for (var index = 0; index < length; index++)
        {
            sorted[index] = items[order[index]];
        }

        Array.Copy(sorted, 0, items, start, length);
    }

    /// <summary>
    ///     Writes output positions <c>[from, to)</c> of the stable merge of the sorted runs
    ///     <c>source[left..left + leftLength)</c> and <c>source[right..right + rightLength)</c> into
    ///     <c>target[left + from..left + to)</c>.
    /// </summary>
    private static void MergeSlice<T>(T[] source, int left, int leftLength, int right, int rightLength, T[] target, int from, int to, Comparison<T> compare)
    {
        var i = SplitPoint(source, left, leftLength, right, rightLength, from, compare);
        var j = from - i;
        var iEnd = SplitPoint(source, left, leftLength, right, rightLength, to, compare);
        var jEnd = to - iEnd;
        var output = left + from;
        while (i < iEnd && j < jEnd)
        {
            // Left first on ties: it holds the earlier input positions.
            if (compare(source[left + i], source[right + j]) <= 0)
            {
                target[output++] = source[left + i++];
            }
            else
            {
                target[output++] = source[right + j++];
            }
        }

        while (i < iEnd)
        {
            target[output++] = source[left + i++];
        }

        while (j < jEnd)
        {
            target[output++] = source[right + j++];
        }
    }

    /// <summary>How many left-run elements the first <paramref name="diagonal" /> merged outputs take.</summary>
    private static int SplitPoint<T>(T[] source, int left, int leftLength, int right, int rightLength, int diagonal, Comparison<T> compare)
    {
        var low = Math.Max(0, diagonal - rightLength);
        var high = Math.Min(diagonal, leftLength);
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            // Left element `middle` precedes right element `diagonal - middle - 1` (ties included):
            // the prefix takes more than `middle` left elements.
            if (compare(source[left + middle], source[right + diagonal - middle - 1]) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
