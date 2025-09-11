namespace Depscan;

public class CallGraph
{
    public List<MethodCallEdge> Edges { get; set; } = new();
    public List<MethodNode> Nodes { get; set; } = new();
}
