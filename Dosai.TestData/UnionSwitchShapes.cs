// Switch shapes over C# 15 unions for the union switch review checks. Each method name says
// whether a finding is expected: Flag* methods hold a switch that throws on an empty union or
// skips a case type, Clean* methods hold one that does not. An empty union is `default`, an
// uninitialized field, or a deserialized empty value: its Value is null.
using System;
using System.Runtime.CompilerServices;

public sealed record Cat(string Name);
public sealed record Dog(string Name);
public union Pet(Cat, Dog);
public union Reading(int, string);
public union Toggle(bool, Cat);
public interface IShape { double Area { get; } }
public sealed record Circle(double Radius) : IShape { public double Area => Radius * Radius * Math.PI; }
public sealed record Square(double Side) : IShape { public double Area => Side * Side; }
public union Shape(Circle, Square);
public sealed record Ok<T>(T Value);
public sealed record Failed(string Reason);
public union Outcome<T>(Ok<T>, Failed);
public sealed record Owner(Pet Pet, string Name);
public sealed record Household(Owner Owner);

// A hand-written union whose cases come only from its member-provider interface.
[Union]
public struct Delivery : Delivery.IUnionMembers
{
    private readonly object? _value;
    private Delivery(object? value, bool _) { _value = value; }
    public interface IUnionMembers
    {
        static abstract Delivery Create(Cat cat);
        static abstract Delivery Create(Dog dog);
        object? Value { get; }
    }
    public static Delivery Create(Cat cat) => new(cat, true);
    public static Delivery Create(Dog dog) => new(dog, true);
    public object? Value => _value;
}

public static class UnionSwitchShapes
{
    // An empty property pattern matches every union value, the empty one included.
    public static string CleanEmptyPropertyArm(Pet pet) => pet switch
    {
        Cat cat => cat.Name,
        { } => "any",
    };

    // `not (Cat or null)` rejects the empty union, so it still throws (CS8655 warns too).
    public static string FlagNegatedOrNull(Pet pet) => pet switch
    {
        Cat cat => cat.Name,
        not (Cat or null) => "dog",
    };

    // `not Cat` accepts the empty union.
    public static string CleanNegatedCase(Pet pet) => pet switch
    {
        Cat cat => cat.Name,
        not Cat => "other",
    };

    public static string FlagNullableWithoutNullArm(Pet? pet) => pet switch
    {
        Cat cat => cat.Name,
        Dog dog => dog.Name,
    };

    public static string CleanNullableWithNullArm(Pet? pet) => pet switch
    {
        Cat cat => cat.Name,
        Dog dog => dog.Name,
        null => string.Empty,
    };

    public static string FlagGenericUnion(Outcome<int> outcome) => outcome switch
    {
        Ok<int> ok => ok.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Failed failed => failed.Reason,
    };

    public static string CleanVarArm(Pet pet) => pet switch
    {
        Cat cat => cat.Name,
        var other => other.ToString(),
    };

    // A union inside a tuple: an empty Pet with any int matches no arm.
    public static string FlagTupleElement((Pet Pet, int Count) pair) => pair switch
    {
        (Cat cat, _) => cat.Name,
        (Dog dog, _) => dog.Name,
    };

    public static string CleanTupleElementWithNullArm((Pet Pet, int Count) pair) => pair switch
    {
        (Cat cat, _) => cat.Name,
        (Dog dog, _) => dog.Name,
        (null, _) => string.Empty,
    };

    // A union reached through properties, written both nested and as an extended pattern.
    public static string FlagNestedProperty(Household household) => household switch
    {
        { Owner: { Pet: Cat cat } } => cat.Name,
        { Owner.Pet: Dog dog } => dog.Name,
        null => string.Empty,
    };

    public static string CleanNestedPropertyWithNullArm(Household household) => household switch
    {
        { Owner.Pet: Cat cat } => cat.Name,
        { Owner.Pet: Dog dog } => dog.Name,
        { Owner.Pet: null } => string.Empty,
        null => string.Empty,
    };

    // An arm that does not test the union position at all lets the empty union through.
    public static string CleanArmIgnoringThePosition(Owner owner) => owner switch
    {
        { Pet: Cat cat } => cat.Name,
        { Name: var name } => name,
    };

    public static void CleanStatementEmptyPropertyClause(Pet pet)
    {
        switch (pet)
        {
            case Cat cat:
                Console.WriteLine(cat.Name);
                break;
            case { }:
                break;
        }
    }

    public static void CleanStatementNotNullClause(Pet pet)
    {
        switch (pet)
        {
            case Cat cat:
                Console.WriteLine(cat.Name);
                break;
            case not null:
                break;
        }
    }

    // `< 0` and `>= 0` together cover every int.
    public static void CleanStatementRelationalCoverage(Reading reading)
    {
        switch (reading)
        {
            case < 0:
                break;
            case >= 0:
                break;
            case string text:
                Console.WriteLine(text);
                break;
        }
    }

    // `< 0` and `> 0` leave out zero.
    public static void FlagStatementRelationalGap(Reading reading)
    {
        switch (reading)
        {
            case < 0:
                break;
            case > 0:
                break;
            case string text:
                Console.WriteLine(text);
                break;
        }
    }

    public static void CleanStatementBooleanConstants(Toggle toggle)
    {
        switch (toggle)
        {
            case true:
                break;
            case false:
                break;
            case Cat:
                break;
        }
    }

    // A type test for an interface both case types implement covers both.
    public static void CleanStatementInterfaceCase(Shape shape)
    {
        switch (shape)
        {
            case IShape any:
                Console.WriteLine(any.Area);
                break;
        }
    }

    public static void FlagStatementMemberProviderUnion(Delivery delivery)
    {
        switch (delivery)
        {
            case Cat cat:
                Console.WriteLine(cat.Name);
                break;
        }
    }

    public static void FlagStatementGenericUnion(Outcome<string> outcome)
    {
        switch (outcome)
        {
            case Ok<string> ok:
                Console.WriteLine(ok.Value);
                break;
        }
    }
}
