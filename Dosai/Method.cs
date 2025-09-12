namespace Depscan;

public class Method
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Assembly {  get; set; }
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
    public string? BaseType { get; set; }
    public List<string>? ImplementedInterfaces { get; set; }
    public int MetadataToken { get; set; }
    public string? SourceSignature { get; set; }
    public string? AssemblySignature { get; set; }
    public bool IsGenericMethod { get; set; }
    public List<string>? GenericParameters { get; set; }
    public bool IsGenericMethodDefinition { get; set; }
}