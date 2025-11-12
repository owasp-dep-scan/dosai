namespace Depscan;

public class Parameter
{
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? TypeFullName { get; set; }
    public bool IsGenericParameter { get; set; }
}