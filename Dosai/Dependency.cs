namespace Depscan;

public class Dependency
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Name { get; set; }
    public string? Assembly { get; set; }
    public string? Module { get; set; }
    public string? Purl { get; set; }
    public string? Namespace { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public List<string>? NamespaceMembers { get; set; }

    /// <summary>
    ///     The purl a script directive pins exactly (<c>#:package Id@1.2.3</c>,
    ///     <c>#r "nuget: Id, 1.2.3"</c>). It wins over the restore tables at enrichment: a
    ///     file-based app or script restores outside the tree, so what the tree restored was
    ///     never its closure. Not serialized: enrichment copies it into <see cref="Purl" />.
    /// </summary>
    internal string? DeclaredPurl { get; set; }
}
