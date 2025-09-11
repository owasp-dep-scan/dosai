namespace Depscan;

public class MethodCallEdge
{
    // Unique identifier for the source node (the method/property/etc making the call)
    // Format: "CallerNamespace.CallerClass.CallerMethod"
    public required string SourceId { get; set; } 

    // Unique identifier for the target node (the method/property/etc being called)
    // Format: "Namespace.ClassName.MemberName" (derived from CalledMethod details)
    public required string TargetId { get; set; } 

    public required CallLocation CallLocation {  get; set; } 
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string? FileName { get; set; }

    // Indicates if the target of the call (Callee) is internal to the analyzed project/source.
    public bool IsInternal { get; set; } 

    // Optional: Store the display name of the called method for reference/debugging
    public string? CalledMethodName { get; set; } 

    // Optional: Store the argument types or expressions involved in the call
    public List<string>? Arguments { get; set; } 
}