namespace Depscan;

public class CallGraph
{
    public List<MethodCallEdge> Edges { get; set; } = [];
    public List<MethodNode> Nodes { get; set; } = [];
}
