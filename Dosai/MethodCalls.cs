namespace Depscan;

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
    public List<string>? ArgumentExpressions { get; set; }
    public CallType CallType { get; set; } = CallType.Unknown;
    public string? CallerMethod { get; set; }
    public string? CallerNamespace { get; set; }
    public string? CallerClass { get; set; }
    public bool IsInternal { get; set; }
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
