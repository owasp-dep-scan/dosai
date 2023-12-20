namespace Dosai;

public class MethodCalls
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Assembly { get; set; }
    public string? Module { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? CalledMethod { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public List<string>? Arguments { get; set; }
}
