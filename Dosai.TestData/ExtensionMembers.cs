// C# 14/15 extension-member source: a classic `this`-parameter extension method alongside an
// `extension(...)` block declaring a method, a property, and a C# 15 extension indexer. Members
// of the extension block are contained in a compiler-synthesized nested type with no metadata
// name, so the source inventory must attribute them to the enclosing static class
// (`SequenceHelpers`) rather than an empty class name. Usage sites exercise both receiver forms.
//
// Source-mode fixture only: compiling it would change Dosai.TestData.CSharp's member set and
// with it the method tables the assembly-mode tests expect.
using System.Collections.Generic;
using System.Linq;

public static class SequenceHelpers
{
    extension(IEnumerable<int> sequence)
    {
        public int CountAtLeast(int minimum) => sequence.Count() >= minimum ? minimum : sequence.Count();

        public int Count => sequence.Count();

        public int this[int index] => sequence.ElementAt(index);
    }

    public static int DoubleCount(this IEnumerable<int> sequence) => sequence.Count() * 2;
}

public static class ExtensionMemberUsers
{
    public static int Use(IEnumerable<int> numbers)
    {
        var indexed = numbers[2];
        var counted = numbers.CountAtLeast(5);
        var total = numbers.Count;
        var doubled = numbers.DoubleCount();
        return indexed + counted + total + doubled;
    }
}
