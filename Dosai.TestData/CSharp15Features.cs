// C# 15 source (ships with .NET 11) exercising the non-union feature set: the `closed`
// modifier with exhaustive switch, extension indexers, collection expression arguments
// (`[with(...), .. values]`), labeled `break`/`continue`, the `unsafe(...)` expression in a
// field initializer, and the pointer relaxations (`&`, `fixed`, `stackalloc`, `sizeof` with no
// `unsafe` context). Taint flows through every construct: each feature method reads its own
// Console.ReadLine source (only `Main` gets its `args` seeded) and must reach Process.Start.
//
// Source-mode fixture only: compiling it would change Dosai.TestData.CSharp's member set and
// with it the method tables the assembly-mode tests expect.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public sealed record Cat(string Name);
public union Pet(Cat)
{
    public string Name() => "pet";
}

public closed record class GateState;
public record class GateClosed : GateState;
public record class GateOpen(string Command) : GateState;

public static class SequenceExtensions
{
    // Extension blocks are C# 14; the indexer inside one is C# 15.
    extension(IEnumerable<string> sequence)
    {
        public string this[int index] => sequence.ElementAt(index);
        public int CountAtLeast(int minimum) => sequence.Count() >= minimum ? minimum : sequence.Count();
    }
}

public static class CSharp15Features
{
    // An `unsafe(...)` expression is allowed where an unsafe block cannot appear syntactically.
    static readonly string BootCommand = unsafe(ReadBootCommand());

    static unsafe string ReadBootCommand()
    {
        int raw = 42;
        int* pointer = &raw;
        int value = *pointer;
        return value.ToString();
    }

    public static void ClosedSwitch()
    {
        GateState state = new GateOpen(Console.ReadLine() ?? string.Empty);
        var command = state switch
        {
            GateClosed => string.Empty,
            GateOpen(var cmd) => cmd,
        };
        Process.Start(command);
    }

    public static void UnionIsPattern()
    {
        Pet pet = new Cat(Console.ReadLine() ?? string.Empty);
        if (pet is Cat(var name))
        {
            Process.Start(name);
        }
    }

    public static void CollectionExpressionArguments()
    {
        var input = Console.ReadLine() ?? string.Empty;
        List<string> names = [with(capacity: 16), input];
        HashSet<string> unique = [with(StringComparer.OrdinalIgnoreCase), input, "Hello"];
        Process.Start(names[0]);
        Process.Start(unique.First());
    }

    public static void LabeledJumps()
    {
        var grid = new[] { new[] { 0, 1 }, new[] { 2, 3 } };
        var found = string.Empty;
        outer: for (int row = 0; row < grid.Length; row++)
        {
            for (int column = 0; column < grid[row].Length; column++)
            {
                if (grid[row][column] == 1)
                {
                    continue outer;
                }

                if (grid[row][column] == 2)
                {
                    found = Console.ReadLine() ?? string.Empty;
                    break outer;
                }
            }
        }
        Process.Start(found);
    }

    public static unsafe int PointerRelaxations()
    {
        int number = 42;
        int* pointer = &number;
        int[] numbers = [10, 20, 30];
        fixed (int* first = numbers)
        {
            if (*first > 0)
            {
                return sizeof(int) + *pointer;
            }
        }
        Span<byte> buffer = stackalloc byte[8];
        return buffer.Length;
    }
}
