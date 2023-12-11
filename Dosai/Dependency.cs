namespace Depscan;

public class Dependency
{
    public string? FileName { get; set; }
    public string? Name { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
}