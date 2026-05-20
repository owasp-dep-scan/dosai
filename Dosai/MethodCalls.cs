namespace Depscan;

public class MethodCalls
{
    public string? Path { get; set; }
    public string? FileName { get; init; }
    public string? Assembly { get; set; }
    public string? Module { get; set; }
    public string? Namespace { get; init; }
    public string? ClassName { get; init; }
    public string? CalledMethod { get; init; }
    public int LineNumber { get; init; }
    public int ColumnNumber { get; init; }
    public List<string>? Arguments { get; init; }
    public List<string>? ArgumentExpressions { get; init; }
    public CallType CallType { get; init; } = CallType.Unknown;
    public string? SourceId { get; init; }
    public string? TargetId { get; init; }
    public string? Purl { get; set; }
    public string? CallerMethod { get; init; }
    public string? CallerNamespace { get; init; }
    public string? CallerClass { get; init; }
    public bool IsInternal { get; set; }
    public AnalysisEvidenceKind EvidenceKind { get; init; } = AnalysisEvidenceKind.Unknown;
    public List<AnalysisEvidence> Evidence { get; init; } = [];
}

public enum CallType
{
    Unknown,
    MethodCall,
    PropertyGet,
    PropertySet,
    EventSubscribe,
    EventUnsubscribe,
    DelegateInvoke,       
    IndexerGet,
    IndexerSet,      
    ConstructorCall,
}
