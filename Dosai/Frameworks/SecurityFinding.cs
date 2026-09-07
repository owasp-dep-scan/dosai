namespace Depscan.Frameworks;

/// <summary>
///     A severity-tagged security finding derived from framework metadata (endpoint findings,
///     MCP transport integrity, configuration security). Additive schema; every finding
///     carries its evidence location and a remediation hint. Ids are content-derived
///     (kind+file+line), positional counters churn whenever any earlier finding appears or
///     disappears, which would break diffs and suppression keys.
/// </summary>
public sealed class SecurityFinding
{
    public required string Id { get; set; }
    /// <summary>Machine-readable kind, e.g. <c>SensitiveUnauthenticatedEndpoint</c>, <c>McpTransportRisk</c>, <c>ConfigSecurity</c>.</summary>
    public required string Kind { get; set; }
    public required string Title { get; set; }
    /// <summary>info / low / medium / high.</summary>
    public string Severity { get; set; } = "Medium";
    public string? Cwe { get; set; }
    public string? EndpointId { get; set; }
    public string? Route { get; set; }
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public string? Evidence { get; set; }
    public string? Remediation { get; set; }
    public string Confidence { get; set; } = ConfidenceTiers.Heuristic;
    public Dictionary<string, string> Properties { get; set; } = [];
}
