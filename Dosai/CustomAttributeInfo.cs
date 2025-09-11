namespace Depscan;

public class CustomAttributeInfo
{
    public string? Name { get; set; }
    public string? FullName { get; set; }
    public List<string>? ConstructorArguments { get; set; }
    public List<NamedArgumentInfo>? NamedArguments { get; set; }
}