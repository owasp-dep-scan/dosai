namespace Depscan;

public class Method
{
    public string? Path { get; init; }
    public string? FileName { get; init; }
    public string? Assembly {  get; init; }
    public string? Module { get; init; }
    public string? Namespace { get; init; }
    public string? ClassName { get; init; }
    public string? Attributes { get; set; }
    public string? Name { get; init; }
    public string? ReturnType { get; init; }
    public int LineNumber { get; init; }
    public int ColumnNumber { get; init; }
    public List<Parameter>? Parameters { get; init; }
    public List<CustomAttributeInfo>? CustomAttributes { get; set; }
    public string? BaseType { get; init; }
    public List<string>? ImplementedInterfaces { get; init; }
    public int MetadataToken { get; init; }
    public string? SourceSignature { get; init; }
    public string? AssemblySignature { get; init; }
    public bool IsGenericMethod { get; init; }
    public List<string>? GenericParameters { get; init; }
    public bool IsGenericMethodDefinition { get; set; }
}