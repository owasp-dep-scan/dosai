namespace Depscan;

public enum AnalysisEvidenceKind
{
    Unknown,
    SourceRoslynDirect,
    SourceRoslynSummary,
    AssemblyReflection,
    AssemblyIlDirect,
    AssemblyIlSummary,
    AssemblyIlVirtualCandidate,
    AssemblyIlDelegateTarget,
    AssemblyIlGeneratedState,
    ExternalSummary,
    FrameworkModel,
    ReflectionHeuristic,
    LanguageFrontend
}

public sealed class MethodIdentity
{
    public string? Id { get; set; }
    public string? SourceSignature { get; set; }
    public string? AssemblySignature { get; set; }
    public string? Symbol { get; set; }
    public string? AssemblyName { get; set; }
    public string? ModuleName { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public int MetadataToken { get; set; }
    public string? Purl { get; set; }
    public List<AnalysisEvidenceKind> Evidence { get; set; } = [];
}

public sealed class AnalysisEvidence
{
    public AnalysisEvidenceKind Kind { get; set; } = AnalysisEvidenceKind.Unknown;
    public string? Source { get; set; }
    public string? Description { get; set; }
    public string Confidence { get; set; } = "Medium";
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
}

public sealed class MethodSummaryEvidence
{
    public required MethodIdentity Method { get; set; }
    public List<int> ReturnParameterIndexes { get; set; } = [];
    public List<int> SinkParameterIndexes { get; set; } = [];
    public List<string> SinkCategories { get; set; } = [];
    public List<string> TaintKinds { get; set; } = [];
    public List<string> FieldPaths { get; set; } = [];
    public AnalysisEvidenceKind EvidenceKind { get; set; } = AnalysisEvidenceKind.Unknown;
    public string Confidence { get; set; } = "Medium";
}

public static class MethodIdentityFactory
{
    public static MethodIdentity FromMethod(Method method, AnalysisEvidenceKind evidenceKind)
    {
        var id = method.SourceSignature ?? method.AssemblySignature ?? BuildId(method.Namespace, method.ClassName, method.Name, method.Parameters, method.ReturnType);
        return new MethodIdentity
        {
            Id = id,
            SourceSignature = method.SourceSignature,
            AssemblySignature = method.AssemblySignature,
            Symbol = id,
            AssemblyName = method.Assembly,
            ModuleName = method.Module,
            Namespace = method.Namespace,
            ClassName = method.ClassName,
            MethodName = method.Name,
            MetadataToken = method.MetadataToken,
            Purl = method.Purl,
            Evidence = [evidenceKind]
        };
    }

    public static MethodIdentity FromParts(string? id, string? sourceSignature, string? assemblySignature, string? symbol, string? assemblyName, string? moduleName, string? namespaceName, string? className, string? methodName, int metadataToken, string? purl, AnalysisEvidenceKind evidenceKind)
    {
        return new MethodIdentity
        {
            Id = id ?? sourceSignature ?? assemblySignature ?? symbol,
            SourceSignature = sourceSignature,
            AssemblySignature = assemblySignature,
            Symbol = symbol ?? id ?? sourceSignature ?? assemblySignature,
            AssemblyName = assemblyName,
            ModuleName = moduleName,
            Namespace = namespaceName,
            ClassName = className,
            MethodName = methodName,
            MetadataToken = metadataToken,
            Purl = purl,
            Evidence = [evidenceKind]
        };
    }

    private static string BuildId(string? namespaceName, string? className, string? methodName, IEnumerable<Parameter>? parameters, string? returnType)
    {
        var typeName = string.Join('.', new[] { namespaceName, className }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var parameterTypes = parameters?.Select(parameter => parameter.TypeFullName ?? parameter.Type ?? string.Empty) ?? [];
        var id = $"{typeName}.{methodName}({string.Join(',', parameterTypes)})";
        if (!string.IsNullOrWhiteSpace(returnType) && methodName != ".ctor")
        {
            id += $":{returnType}";
        }
        return id;
    }
}
