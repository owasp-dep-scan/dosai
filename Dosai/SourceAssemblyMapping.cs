namespace Depscan;

/// <summary>
/// Represents a mapping between a source code element and its compiled representation in an assembly.
/// </summary>
public class SourceAssemblyMapping
{
    /// <summary>
    /// Gets or sets the unique identifier for the source element (e.g., "Namespace.Class.Member").
    /// This corresponds to the Id used in MethodNode for source elements.
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// Gets or sets the path to the source file.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the line number in the source file where the element is declared.
    /// </summary>
    public int SourceLineNumber { get; set; }

    /// <summary>
    /// Gets or sets the column number in the source file where the element is declared.
    /// </summary>
    public int SourceColumnNumber { get; set; }
    
    /// <summary>
    /// Gets or sets the unique signature of the source element.
    /// Format: Namespace.ClassName.MemberName[[GenericParameters]]([ParameterTypes,...]):ReturnType (for methods/ctors)
    ///         Namespace.ClassName.MemberName:Type (for fields/properties/events)
    /// </summary>
    public string? SourceSignature { get; set; }

    /// <summary>
    /// Gets or sets the Metadata Token of the element as determined during source analysis.
    /// This might be 0 if not available from Roslyn during analysis.
    /// </summary>
    public int SourceMetadataToken { get; set; }

    /// <summary>
    /// Gets or sets the Metadata Token of the corresponding element found in the compiled assembly via reflection.
    /// </summary>
    public int AssemblyMetadataToken { get; set; }

    /// <summary>
    /// Gets or sets the full name of the assembly containing the compiled element.
    /// </summary>
    public string? AssemblyName { get; set; }

    /// <summary>
    /// Gets or sets the name of the module within the assembly containing the compiled element.
    /// </summary>
    public string? ModuleName { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier for the assembly element (e.g., "Namespace.Class.Member").
    /// This could be constructed similarly to SourceId or based on reflection data.
    /// </summary>
    public string? AssemblyId { get; set; }
    
    /// <summary>
    /// Gets or sets the unique signature of the assembly element (as derived from reflection).
    /// Format: Namespace.ClassName.MemberName[[GenericParameters]]([ParameterTypes,...]):ReturnType (for methods/ctors)
    ///         Namespace.ClassName.MemberName:Type (for fields/properties/events)
    /// </summary>
    public string? AssemblySignature { get; set; }

    /// <summary>
    /// Gets or sets the type of member this mapping represents (Method, Property, Field, Event, Constructor).
    /// </summary>
    public string? MemberType { get; set; } // e.g., "Method", "Property"

    /// <summary>
    /// Gets or sets the name of the member.
    /// </summary>
    public string? MemberName { get; set; }

    /// <summary>
    /// Gets or sets the name of the class containing the member.
    /// </summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// Gets or sets the namespace of the member.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a valid mapping was found based on signature matching.
    /// </summary>
    public bool IsMapped { get; set; }
}