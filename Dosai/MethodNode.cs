namespace Depscan;

public class MethodNode
{
    public required string Id { get; set; } // Unique identifier (e.g., Namespace.ClassName.MethodName)
    public required string Name { get; set; }
    public required string ClassName { get; set; }
    public required string Namespace { get; set; }
    public required string FileName { get; set; }
}