namespace Depscan.Frameworks;

/// <summary>
///     An AI-related inventory entry: model, dataset, prompt, tool, agent, guardrail, or embedding.
///     Maps to CycloneDX machine-learning-model components with modelCard data.
/// </summary>
public sealed class AiComponent
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"model", "dataset", "prompt", "tool", "agent", "guardrail", or "embedding".</summary>
    public string Kind { get; set; } = "model";

    /// <summary>Model identifier or artifact name, e.g. "gpt-4o", "all-MiniLM-L6-v2", "phi-3.gguf".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"openai", "azure", "anthropic", "local", "huggingface", ...</summary>
    public string? Provider { get; set; }

    public string? Version { get; set; }

    /// <summary>e.g. pkg:huggingface/... where derivable.</summary>
    public string? Purl { get; set; }

    public string? Task { get; set; }

    public string? ArchitectureFamily { get; set; }

    public string? ModelArchitecture { get; set; }

    public List<string> InputFormats { get; set; } = [];

    public List<string> OutputFormats { get; set; } = [];

    /// <summary>"local" or "remote".</summary>
    public string? Deployment { get; set; }

    /// <summary>For on-disk artifacts such as .onnx/.gguf files.</summary>
    public string? FilePath { get; set; }

    public string? Sha256 { get; set; }

    /// <summary>JSON Schema of the tool parameters, for Kind == "tool".</summary>
    public string? ToolSchema { get; set; }

    /// <summary>
    ///     SHA-256 prefix and the first 200 characters by default; full text only under
    ///     --include-prompt-text because system prompts can contain secrets and proprietary IP.
    /// </summary>
    public string? PromptText { get; set; }

    public List<string> ServiceIds { get; set; } = [];

    public string Confidence { get; set; } = ConfidenceTiers.Syntactic;

    public AnalysisEvidence Evidence { get; set; } = new();

    public CodeLocation Location { get; set; } = new();

    public Dictionary<string, string> Properties { get; set; } = [];
}
