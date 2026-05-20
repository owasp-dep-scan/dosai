namespace Depscan;

public class MethodNode
{
    public required string Id { get; set; } // Stable symbol signature, including overload parameters when available.
    public required string Name { get; set; }
    public string? Label { get; set; }
    public required string ClassName { get; set; }
    public required string Namespace { get; set; }
    public string? Assembly { get; set; }
    public string? Module { get; set; }
    public string? Purl { get; set; }
    public required string FileName { get; set; }
    public string Kind { get; set; } = "Method";
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public bool IsExternal { get; set; }
    public MethodIdentity? Identity { get; set; }
    public List<AnalysisEvidence> Evidence { get; set; } = [];
}