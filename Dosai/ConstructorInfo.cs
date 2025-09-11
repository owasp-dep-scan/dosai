namespace Depscan;

public class ConstructorInfo
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Assembly { get; set; }
    public string? Module { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? Attributes { get; set; }
    public string? Name { get; set; }
    public string? ReturnType { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public List<Parameter>? Parameters { get; set; }
    public List<CustomAttributeInfo>? CustomAttributes { get; set; }
    public bool IsStatic { get; set; }
    public string? BaseType { get; set; }
    public List<string>? ImplementedInterfaces { get; set; }
}