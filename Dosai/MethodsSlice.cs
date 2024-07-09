namespace Depscan;

public class MethodsSlice
{
	public List<Dependency>? Dependencies {  get; set; }

	public List<Method>? Methods { get; set; }

	public List<MethodCalls>? MethodCalls { get; set; }

	public List<AssemblyInformation>? AssemblyInformation { get; set; }
}
