using Depscan;
using Depscan.Frameworks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Dosai.Tests.Frameworks;

/// <summary>
///     .NET 11 / C# 15 union DTOs and JSON closed-type polymorphism are (de)serialization
///     boundaries: inbound JSON binds into every union case and polymorphic payload, so the
///     data classifications of those payload members must surface, not just the declared
///     members of the boundary type itself.
/// </summary>
public class UnionClassificationTests
{
    private const string Source = """
public sealed class JsonPolymorphicAttribute : System.Attribute { }
public sealed class JsonUnionAttribute : System.Attribute { }

public sealed record Success(string Email, string Password);
public sealed record Failure(int ErrorCode);
public union Result(Success, Failure);

[JsonPolymorphic]
public interface IPayment
{
    ICard Card { get; }
}

public interface ICard
{
    string CardNumber { get; }
}

public interface IReceipt
{
    ICard Card { get; }
}

public sealed record Signup(string Email, string ApiKey);
public sealed record Guest(string Nickname);

// A hand-written union whose cases are named only by its member-provider interface: it has no
// public single-parameter constructor, so the cases come from IUnionMembers.Create.
[System.Runtime.CompilerServices.Union]
public struct Registration : Registration.IUnionMembers
{
    private readonly object? _value;
    private Registration(object? value, bool _) { _value = value; }
    public interface IUnionMembers
    {
        static abstract Registration Create(Signup signup);
        static abstract Registration Create(Guest guest);
        object? Value { get; }
    }
    public static Registration Create(Signup signup) => new(signup, true);
    public static Registration Create(Guest guest) => new(guest, true);
    public object? Value => _value;
}

// A hand-written union with public case constructors; the private one is not a creation member.
[System.Runtime.CompilerServices.Union]
public struct Login : System.Runtime.CompilerServices.IUnion
{
    private readonly object? _value;
    public Login(Signup signup) { _value = signup; }
    private Login(Success success) { _value = success; }
    public object? Value => _value;
}
""";

    [Fact]
    public void ClassifyAll_UnionDto_SurfacesCaseRecordMembers()
    {
        var unionSymbol = ResolveSymbol<INamedTypeSymbol>("Result");

        var classifications = DataClassifier.ClassifyAll(unionSymbol);

        // The union declares no members of its own; Password and Email live on the Success
        // case record that inbound JSON can bind into.
        Assert.Contains(("credential", (string?)"Password"), classifications);
        Assert.Contains(("pii", (string?)"Email"), classifications);
    }

    [Fact]
    public void ClassifyAll_MemberProviderUnion_SurfacesCaseMembersFromCreateMethods()
    {
        var classifications = DataClassifier.ClassifyAll(ResolveSymbol<INamedTypeSymbol>("Registration"));

        Assert.Contains(("credential", (string?)"ApiKey"), classifications);
        Assert.Contains(("pii", (string?)"Email"), classifications);
    }

    [Fact]
    public void ClassifyAll_HandWrittenUnion_ReadsOnlyPublicCaseConstructors()
    {
        var classifications = DataClassifier.ClassifyAll(ResolveSymbol<INamedTypeSymbol>("Login"));

        Assert.Contains(("credential", (string?)"ApiKey"), classifications);
        // Success reaches Login only through a private constructor, which is not a case.
        Assert.DoesNotContain(("credential", (string?)"Password"), classifications);
    }

    [Fact]
    public void CaseTypes_FollowTheCompilersCreationMembers()
    {
        static string[] Names(string type) => UnionShapes.CaseTypes((INamedTypeSymbol)ResolveSymbol<INamedTypeSymbol>(type)).Select(caseType => caseType.Name).ToArray();

        Assert.Equal(["Success", "Failure"], Names("Result"));
        Assert.Equal(["Signup", "Guest"], Names("Registration"));
        Assert.Equal(["Signup"], Names("Login"));
    }

    [Fact]
    public void ClassifyAll_JsonPolymorphicBoundary_IncludesInterfacePayloadMembers()
    {
        var paymentSymbol = ResolveSymbol<INamedTypeSymbol>("IPayment");
        var receiptSymbol = ResolveSymbol<INamedTypeSymbol>("IReceipt");

        Assert.Contains(("financial", (string?)"CardNumber"), DataClassifier.ClassifyAll(paymentSymbol));
        // Without the boundary marker the interface-typed member is not followed, the
        // attribute is what makes this a (de)serialization boundary.
        Assert.DoesNotContain(("financial", (string?)"CardNumber"), DataClassifier.ClassifyAll(receiptSymbol));
    }

    private static ITypeSymbol ResolveSymbol<TSymbol>(string name) where TSymbol : ITypeSymbol
    {
        var tree = CSharpSourceParser.Parse(Source, "Dtos.cs");
        var compilation = CSharpCompilation.Create(
            "Dosai.UnionClassificationTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First(node => node.Identifier.Text == name);
        var symbol = model.GetDeclaredSymbol(declaration);
        Assert.NotNull(symbol);
        return symbol!;
    }
}
