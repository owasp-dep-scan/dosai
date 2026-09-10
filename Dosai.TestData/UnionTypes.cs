// C# 15 source exercising .NET 11 union declarations: the union declaration itself,
// a method declared on the union body, and taint flowing through both the switch
// statement and switch expression forms of union pattern matching. Analyzers must
// inventory every declared method here and propagate taint from the switch operand
// to the pattern-bound payload locals.
using System;
using System.Diagnostics;

public sealed record Success(string Message);
public sealed record Failure(int ErrorCode);

public union Result(Success, Failure)
{
    public string Describe() => "result";
}

public static class UnionTypes
{
    public static void Main(string[] args)
    {
        Result parsed = new Success(args[0]);

        switch (parsed)
        {
            case Success(var message):
                Process.Start(message);
                break;
            case Failure:
                break;
        }
    }

    public static void Describe()
    {
        Result parsed = new Success(Console.ReadLine() ?? string.Empty);
        var command = parsed switch
        {
            Success(var message) => message,
            Failure => string.Empty,
        };
        Process.Start(command);
    }
}
