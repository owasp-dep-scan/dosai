namespace Depscan;

public class CustomAttributeInfo
{
    public string? Name { get; set; }
    public string? FullName { get; set; }

    /// <summary>Structured since schema 5.1.0 - see <see cref="CustomAttributeArgumentInfo" /> for the encoding.</summary>
    public List<CustomAttributeArgumentInfo>? ConstructorArguments { get; set; }

    public List<NamedArgumentInfo>? NamedArguments { get; set; }
}
