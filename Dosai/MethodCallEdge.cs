namespace Depscan;

public class MethodCallEdge
{
    // Unique identifier for the source node (the method/property/etc making the call)
    // Format: "CallerNamespace.CallerClass.CallerMethod"
    public required string SourceId { get; set; } 

    // Unique identifier for the target node (the method/property/etc being called)
    public required string TargetId { get; set; } 

    public required CallLocation CallLocation {  get; set; } 
    public string? FileName { get; set; }

    public bool IsInternal { get; set; } 

    public string? CalledMethodName { get; set; } 

    public List<string>? Arguments { get; set; } 
    public List<string>? ArgumentExpressions { get; set; }
    public CallType CallType { get; set; } = CallType.Unknown;
}