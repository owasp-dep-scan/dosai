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
