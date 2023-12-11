namespace Depscan;

public class Dependency
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Name { get; set; }
    public string? Assembly { get; set; }
    public string? Module { get; set; }
    public string? Namespace { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public List<string>? NamespaceMembers { get; set; }
}
