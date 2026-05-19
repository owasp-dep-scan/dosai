namespace Depscan;

public class MethodsSlice
{
    public AnalysisMetadata? Metadata { get; init; }

    public List<Dependency>? Dependencies {  get; init; }

    public List<Method>? Methods { get; init; }

    public List<MethodCalls>? MethodCalls { get; init; }

    public List<AssemblyInformation>? AssemblyInformation { get; init; }
	
    public List<PropertyInfo>? Properties { get; init; }
	
    public List<FieldInfo>? Fields { get; init; }
	
    public List<EventInfo>? Events { get; init; }
	
    public List<ConstructorInfo>? Constructors { get; init; }
    
    public CallGraph? CallGraph { get; init; }
    public List<ApiEndpoint>? ApiEndpoints { get; init; }
    public List<EntryPoint>? EntryPoints { get; init; }
    public List<PackageReachability>? PackageReachability { get; init; }
    public List<SourceAssemblyMapping>? SourceAssemblyMapping { get; init; }
}