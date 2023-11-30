namespace Depscan;

public class Method
{
    public string? FileName { get; set; }
    public string? Namespace { get; set; }
    public string? Class { get; set; }
    public string? Attributes { get; set; }
    public string? Name { get; set; }
    public string? ReturnType { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public List<Parameter>? Parameters { get; set; }
}