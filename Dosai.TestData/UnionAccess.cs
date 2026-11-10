// C# 15 union access shapes beyond the positional switch arms in UnionTypes.cs: reading the
// `Value` property, a type pattern, a nested property pattern, and a hand-written [Union] type
// read through TryGetValue. Taint must reach the sink through each of them. The switches at
// the end exercise the union switch review checks: a switch expression with no null arm
// throws SwitchExpressionException on a default union, and a switch statement that skips a
// case type is not reported by the compiler.
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

public sealed record Accepted(string Command);
public sealed record Rejected(string Reason);
public union Verdict(Accepted, Rejected);

[Union]
public struct Envelope : IUnion
{
    private readonly object? _value;
    public Envelope(Accepted accepted) { _value = accepted; }
    public Envelope(Rejected rejected) { _value = rejected; }
    public object? Value => _value;
    public bool TryGetValue(out Accepted? accepted)
    {
        accepted = _value as Accepted;
        return accepted is not null;
    }
}

public static class UnionAccess
{
    public static void ThroughValueProperty()
    {
        Verdict verdict = new Accepted(Console.ReadLine()!);
        if (verdict.Value is Accepted viaValue)
        {
            Process.Start(viaValue.Command);
        }
    }

    public static void ThroughTypePattern()
    {
        Verdict verdict = new Rejected(Console.ReadLine()!);
        switch (verdict)
        {
            case Rejected viaTypePattern:
                Process.Start(viaTypePattern.Reason);
                break;
            case Accepted:
                break;
            case null:
                break;
        }
    }

    public static void ThroughPropertyPattern()
    {
        Verdict verdict = new Rejected(Console.ReadLine()!);
        var viaPropertyPattern = verdict switch
        {
            Rejected { Reason: var why } => why,
            Accepted => string.Empty,
            null => string.Empty,
        };
        Process.Start(viaPropertyPattern);
    }

    public static void ThroughTryGetValue()
    {
        var envelope = new Envelope(new Accepted(Console.ReadLine()!));
        if (envelope.TryGetValue(out var viaTryGetValue))
        {
            Process.Start(viaTryGetValue!.Command);
        }
    }

    // Finding: no null arm, and the switched value comes from the console.
    public static string WithoutNullArm()
    {
        Verdict verdict = new Accepted(Console.ReadLine()!);
        return verdict switch
        {
            Accepted accepted => accepted.Command,
            Rejected rejected => rejected.Reason,
        };
    }

    // Finding: no null arm on a default union, with no source data involved.
    public static string DefaultUnion()
    {
        Verdict verdict = default;
        return verdict switch
        {
            Accepted accepted => accepted.Command,
            Rejected(var reason) => reason,
        };
    }

    // No finding: a discard catches the empty union.
    public static string WithDiscard(Verdict verdict) => verdict switch
    {
        Accepted accepted => accepted.Command,
        _ => string.Empty,
    };

    // No finding: the arm matching null is guarded by `or`.
    public static string WithNullAlternative(Verdict verdict) => verdict switch
    {
        Accepted accepted => accepted.Command,
        Rejected or null => string.Empty,
    };

    // Finding: Rejected has no clause, the only arm for it is guarded, and there is no default.
    public static void StatementMissingCase(Verdict verdict)
    {
        switch (verdict)
        {
            case Accepted accepted:
                Console.WriteLine(accepted.Command);
                break;
            case Rejected rejected when rejected.Reason.Length > 0:
                Console.WriteLine(rejected.Reason);
                break;
        }
    }

    // No finding: a default clause handles everything else.
    public static void StatementWithDefault(Verdict verdict)
    {
        switch (verdict)
        {
            case Accepted accepted:
                Console.WriteLine(accepted.Command);
                break;
            default:
                break;
        }
    }

    // No finding: every case type has an unconditional clause; a statement never throws.
    public static void StatementCoveringEveryCase(Envelope envelope)
    {
        switch (envelope)
        {
            case Accepted(var command):
                Console.WriteLine(command);
                break;
            case Rejected { Reason: _ }:
                break;
        }
    }

    // No finding: a switch over a class hierarchy, not a union.
    public static string NotAUnion(object value) => value switch
    {
        string text => text,
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Empty,
    };
}
