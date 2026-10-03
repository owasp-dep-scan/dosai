using Depscan;
using Xunit;

namespace Dosai.Tests;

public class ParallelSortTests
{
    [Theory]
    [InlineData(0, 4, 1)]
    [InlineData(1, 4, 1)]
    [InlineData(17, 3, 2)]
    [InlineData(1000, 7, 50)]
    [InlineData(4099, 5, 128)]
    [InlineData(50_000, 16, 1000)]
    [InlineData(50_000, 13, 700)]
    public void StableSort_EqualsASequentialStableSortForAnyRunLayout(int count, int workers, int minRunLength)
    {
        // Few distinct keys, so most comparisons tie and only stability decides the order; the
        // payload records each element's input position.
        var rng = new Random(count + workers);
        var items = Enumerable.Range(0, count).Select(position => (Key: rng.Next(Math.Max(1, count / 20)), Position: position)).ToArray();
        var expected = items.OrderBy(item => item.Key).ToArray();

        ParallelSort.StableSort(items, (x, y) => x.Key.CompareTo(y.Key), workers, minRunLength);

        Assert.Equal(expected, items);
    }

    [Fact]
    public void StableSort_KeepsAlreadySortedAndReversedInputsStable()
    {
        foreach (var keys in new[] { Enumerable.Range(0, 30_000).Select(i => i / 7), Enumerable.Range(0, 30_000).Select(i => (30_000 - i) / 7) })
        {
            var items = keys.Select((key, position) => (Key: key, Position: position)).ToArray();
            var expected = items.OrderBy(item => item.Key).ToArray();
            ParallelSort.StableSort(items, (x, y) => x.Key.CompareTo(y.Key), workers: 6, minRunLength: 1000);
            Assert.Equal(expected, items);
        }
    }
}
