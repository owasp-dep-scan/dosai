namespace Depscan;

public class MethodCallEdge
{
    public string? Id { get; set; }

    // Unique identifier for the source node (the method/property/etc making the call)
    // Format: stable Roslyn/reflection signature
    public required string SourceId { get; set; } 

    // Unique identifier for the target node (the method/property/etc being called)
    public required string TargetId { get; set; } 

    public required CallLocation CallLocation {  get; set; } 
    public string? Path { get; set; }
    public string? FileName { get; set; }

    public bool IsInternal { get; set; } 

    public string? CalledMethodName { get; set; } 

    public string? SourceName { get; set; }
    public string? TargetName { get; set; }
    public string? SourcePurl { get; set; }
    public string? TargetPurl { get; set; }

    public List<string>? Arguments { get; set; }
    public List<string>? ArgumentExpressions { get; set; }
    public CallType CallType { get; set; } = CallType.Unknown;
    public AnalysisEvidenceKind EvidenceKind { get; set; } = AnalysisEvidenceKind.Unknown;
    public List<AnalysisEvidence> Evidence { get; set; } = [];
    /// <summary>Distinct call sites (file:line:col) for this (source, target, callType, evidence) pair after same-pair collapsing (R7).</summary>
    public int CallSiteCount { get; set; } = 1;
    /// <summary>Dispatch resolution tier for inferred virtual/interface edges: exact / rta-candidate / cha-candidate (R4).</summary>
    public string? DispatchConfidence { get; set; }
}