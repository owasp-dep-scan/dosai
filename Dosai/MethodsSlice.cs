namespace Depscan;

public class MethodsSlice
{
    public List<Dependency>? Dependencies {  get; set; }

    public List<Method>? Methods { get; set; }

    public List<MethodCalls>? MethodCalls { get; set; }

    public List<AssemblyInformation>? AssemblyInformation { get; set; }
	
    public List<PropertyInfo>? Properties { get; set; }
	
    public List<FieldInfo>? Fields { get; set; }
	
    public List<EventInfo>? Events { get; set; }
	
    public List<ConstructorInfo>? Constructors { get; set; }
    
    public CallGraph? CallGraph { get; set; }
    public List<SourceAssemblyMapping>? SourceAssemblyMapping { get; set; }
}