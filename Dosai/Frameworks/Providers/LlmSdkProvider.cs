using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     LLM SDKs and agent frameworks: Microsoft.Extensions.AI, Semantic Kernel, OpenAI/Azure OpenAI,
///     Anthropic, Bedrock, LangChain, Kernel Memory. Extracts model identifiers as AI components,
///     inference endpoints as outbound services, tools exposed to models, system prompts (redacted
///     by default), and agent definitions.
/// </summary>
public sealed partial class LlmSdkProvider : IFrameworkProvider
{
    private static readonly string[] KnownModelPrefixes = ["gpt-", "gpt_", "chatgpt", "o1-", "o3-", "o4-", "claude-", "gemini-", "llama", "mistral", "mixtral", "phi-", "deepseek", "qwen", "text-embedding", "all-minilm", "all-mpnet", "bge-", "nomic-", "whisper", "davinci", "curie"];

    /// <summary>Family markers that identify a model id without anchoring at the start.</summary>
    private static readonly string[] KnownModelInfixes = ["-sonnet", "-opus", "-haiku", "-turbo", "-instruct"];
    private static readonly string[] ToolConstructionCalls = ["AIFunctionFactory.Create", "ImportPluginFromType", "ImportPluginFromPromptDirectory", "ImportPluginFromOpenApi", "CreateFunctionFromMethod"];

    public string Id => "llm";

    public string DisplayName => "LLM SDKs (Extensions.AI, Semantic Kernel, OpenAI, Anthropic, Bedrock)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "IChatClient", "ChatClient", "ChatCompletion", "Kernel", "SemanticKernel", "OpenAI", "Anthropic", "Bedrock", "AIChatClient", "IEmbeddingGenerator", "EmbeddingGenerator", "AIFunction", "ChatOptions", "InvokePrompt", "GetResponse", "CompleteChat", "AsIChatClient", "prompty", "ChatMessage"))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            var model = ctx.CSharp!.GetSemanticModel(tree);

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                AnalyzeInvocation(ctx, results, invocation, tree, model);
            }

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                AnalyzeCreation(ctx, results, creation, tree);
            }

            // System prompts: string literals passed as system messages. `Create` is deliberately NOT
            // matched bare — it is one of the most common method names in .NET, and matching it meant
            // any long string argument to any Create() in a file that merely mentioned "OpenAI" was
            // recorded as a system prompt. Connection strings were being harvested that way.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsSystemPromptCall))
            {
                var prompt = ProviderHelpers.StringArguments(invocation).FirstOrDefault(value => value.Length > 40);
                if (prompt is not null)
                {
                    AddPrompt(ctx, results, prompt, tree.FilePath, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }
            }

            // Literal system-role message construction: ChatMessage(ChatRole.System, "...")
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeName = creation.Type.ToString();
                if (!typeName.Contains("ChatMessage") && !typeName.Contains("ChatMessageContent"))
                {
                    continue;
                }

                // new ChatMessage { ... } has no argument list; only parenthesized forms carry one.
                if (creation.ArgumentList is not { Arguments: { Count: 2 } arguments })
                {
                    continue;
                }

                if (arguments[0].Expression.ToString().Contains("System", StringComparison.Ordinal) && arguments[1].Expression is LiteralExpressionSyntax literal && literal.Token.Value is string prompt && prompt.Length > 40)
                {
                    AddPrompt(ctx, results, prompt, tree.FilePath, creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }
            }
        }

        // .prompty files are prompt definitions on disk.
        foreach (var promptFile in ctx.ConfigFiles.Where(file => file.EndsWith(".prompty", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                AddPrompt(ctx, results, File.ReadAllText(promptFile), promptFile, 1);
            }
            catch (IOException)
            {
                // Unreadable prompt file: skip.
            }
        }
    }

    private static void AnalyzeInvocation(FrameworkContext ctx, FrameworkResults results, InvocationExpressionSyntax invocation, SyntaxTree tree, SemanticModel model)
    {
        var name = ProviderHelpers.InvocationName(invocation);
        switch (name)
        {
            // Construction of chat clients with model ids: client.CompleteChat("gpt-4o"...) /
            // GetResponseAsync(...) with model literal / deploymentName: "...".
            case "CompleteChatAsync" or "CompleteChat" or "GetResponseAsync" or "GenerateName":
            case "InvokePromptAsync" or "InvokeFunctionAsync":
                var modelLiteral = ProviderHelpers.StringArguments(invocation).FirstOrDefault(IsModelIdentifier);
                if (modelLiteral is not null)
                {
                    AddModel(ctx, results, modelLiteral, null, tree, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }

                break;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var argumentName = argument.NameColon?.Name.Identifier.Text;
            var literal = argument.Expression as LiteralExpressionSyntax;
            if (literal is null || literal.Token.Value is not string value)
            {
                continue;
            }

            if (argumentName is "deploymentName" or "modelId" or "model" or "modelId" && IsModelIdentifier(value))
            {
                AddModel(ctx, results, value, null, tree, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }

        if (ToolConstructionCalls.Any(call => invocation.ToString().Contains(call, StringComparison.Ordinal)))
        {
            var enclosingType = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "tool";
            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            results.AiComponents.Add(new AiComponent
            {
                Id = FrameworkIds.Ai("tool", "llm", enclosingType),
                Kind = "tool",
                Name = enclosingType,
                Provider = "llm",
                ServiceIds = [],
                Confidence = ConfidenceTiers.Syntactic,
                Location = CodeLocation.From(ctx.BasePath, tree.FilePath, line),
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "llm", Description = "Tool/function exposed to a model.", Confidence = ConfidenceTiers.Syntactic }
            });
        }
    }

    private static void AnalyzeCreation(FrameworkContext ctx, FrameworkResults results, ObjectCreationExpressionSyntax creation, SyntaxTree tree)
    {
        var typeName = creation.Type.ToString();
        var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Chat agents become agent components.
        if (typeName.Contains("ChatCompletionAgent") || typeName.Contains("AzureAIAgent") || typeName.Contains("OpenAIAssistantAgent") || typeName.Contains("AgentGroupChat"))
        {
            var name = ProviderHelpers.StringArguments(creation.ArgumentList).FirstOrDefault() ?? typeName;
            results.AiComponents.Add(new AiComponent
            {
                Id = FrameworkIds.Ai("agent", "llm", name),
                Kind = "agent",
                Name = name,
                Provider = "llm",
                Confidence = ConfidenceTiers.Syntactic,
                Location = CodeLocation.From(ctx.BasePath, tree.FilePath, line),
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "llm", Description = "Agent definition.", Confidence = ConfidenceTiers.Syntactic }
            });
        }

        // Model ids in constructor arguments (e.g. new ChatClient("gpt-4o")).
        var modelLiteral = LiteralArguments(creation.ArgumentList).FirstOrDefault(IsModelIdentifier);
        if (modelLiteral is not null)
        {
            AddModel(ctx, results, modelLiteral, null, tree, line);
        }

        // Endpoints: new AzureOpenAIClient(new Uri("...")), new Uri(...) into client options, Ollama base addresses.
        foreach (var url in ctx.RawUrlsFor(tree).Where(url => url.Contains("openai", StringComparison.OrdinalIgnoreCase) || url.Contains("anthropic", StringComparison.OrdinalIgnoreCase) || url.Contains("11434", StringComparison.Ordinal) || url.Contains("bedrock", StringComparison.OrdinalIgnoreCase) || url.Contains("azure", StringComparison.OrdinalIgnoreCase)))
        {
            var serviceId = FrameworkIds.Service("llm", null, $"inference-{HostOf(url)}");
            if (results.Services.Any(service => service.Id == serviceId))
            {
                continue;
            }

            var service = new ServiceComponent
            {
                Id = serviceId,
                Name = $"AI inference ({HostOf(url)})",
                ServiceKind = ServiceKinds.AiInference,
                Direction = ServiceDirections.Outbound,
                Framework = "llm",
                Provider = ProviderOf(url),
                Confidence = ConfidenceTiers.Syntactic,
                Endpoints = [url],
                Location = CodeLocation.From(ctx.BasePath, tree.FilePath, line),
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "llm", Description = $"Inference endpoint {url}.", Confidence = ConfidenceTiers.Syntactic }
            };
            results.Services.Add(service);
        }
    }

    private static List<string> LiteralArguments(ArgumentListSyntax? argumentList) => argumentList?.Arguments
        .Select(argument => argument.Expression)
        .OfType<LiteralExpressionSyntax>()
        .Where(literal => literal.Token.Value is string)
        .Select(literal => (string)literal.Token.Value!)
        .ToList() ?? [];

    /// <summary>
    ///     A prompt-bearing call. Unqualified <c>Create</c> is excluded; only receivers that actually
    ///     belong to a chat/prompt API qualify.
    /// </summary>
    private static bool IsSystemPromptCall(InvocationExpressionSyntax invocation)
    {
        var name = ProviderHelpers.InvocationName(invocation);
        if (name is "AddSystemMessage" or "AddUserMessage" or "SendMessage")
        {
            return true;
        }

        if (name is not "Create")
        {
            return false;
        }

        return invocation.Expression is MemberAccessExpressionSyntax member &&
               member.Expression.ToString() is "ChatMessage" or "TextContent" or "ChatPromptTemplate";
    }

    /// <summary>
    ///     Model identifiers are short, hyphenated names from a known family. The former
    ///     <c>Contains("-mini")</c> fallback matched <c>bundle-minified.js</c>, <c>-minimal</c> and
    ///     <c>-minimum</c>; a bare <c>all-</c> prefix matched <c>all-users</c> and reported it as a
    ///     HuggingFace embedding model.
    /// </summary>
    internal static bool IsModelIdentifier(string value) =>
        value.Length < 40 &&
        !value.Contains('/') && !value.Contains(' ') && !value.Contains('.') &&
        (KnownModelPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
         KnownModelInfixes.Any(infix => value.Contains(infix, StringComparison.OrdinalIgnoreCase)));

    internal static string? ProviderOf(string modelOrUrl) => modelOrUrl switch
    {
        _ when modelOrUrl.Contains("azure", StringComparison.OrdinalIgnoreCase) => "azure",
        _ when modelOrUrl.Contains("anthropic", StringComparison.OrdinalIgnoreCase) || modelOrUrl.StartsWith("claude", StringComparison.OrdinalIgnoreCase) => "anthropic",
        _ when modelOrUrl.Contains("bedrock", StringComparison.OrdinalIgnoreCase) => "bedrock",
        _ when modelOrUrl.Contains("11434", StringComparison.Ordinal) => "ollama",
        _ when modelOrUrl.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || modelOrUrl.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || modelOrUrl.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || modelOrUrl.StartsWith("o4", StringComparison.OrdinalIgnoreCase) || modelOrUrl.Contains("openai", StringComparison.OrdinalIgnoreCase) => "openai",
        _ when modelOrUrl.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) => "google",
        _ when modelOrUrl.StartsWith("text-embedding", StringComparison.OrdinalIgnoreCase) || modelOrUrl.StartsWith("all-", StringComparison.OrdinalIgnoreCase) || modelOrUrl.StartsWith("bge-", StringComparison.OrdinalIgnoreCase) => "huggingface",
        _ => null
    };

    private static void AddModel(FrameworkContext ctx, FrameworkResults results, string modelId, string? version, SyntaxTree tree, int line)
    {
        var id = FrameworkIds.Ai("model", ProviderOf(modelId) ?? "unknown", modelId);
        if (results.AiComponents.Any(component => component.Id == id))
        {
            return;
        }

        results.AiComponents.Add(new AiComponent
        {
            Id = id,
            Kind = "model",
            Name = modelId,
            Provider = ProviderOf(modelId) ?? "unknown",
            Version = version,
            Task = modelId.Contains("embedding", StringComparison.OrdinalIgnoreCase) ? "embedding" : "chat-completion",
            Deployment = "remote",
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, tree.FilePath, line),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "llm", Description = $"Model identifier '{modelId}' in a chat/embedding call.", Confidence = ConfidenceTiers.Syntactic, FileName = Path.GetFileName(tree.FilePath), LineNumber = line }
        });
    }

    /// <summary>
    ///     System prompts can contain secrets and proprietary IP: by default emit a SHA-256 and the
    ///     first 200 characters only; full text under FrameworkAnalysisOptions.IncludePromptText.
    /// </summary>
    /// <summary>
    ///     Conservative check for text that must never be echoed into an SBOM: connection strings,
    ///     key-value credential pairs, PEM blocks, and common token shapes. Prompts are prose; these
    ///     are not, so the false-positive cost is losing a prompt preview, which is the safe direction.
    /// </summary>
    private static bool LooksLikeSecret(string text) =>
        SecretMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
        text.StartsWith("-----BEGIN", StringComparison.Ordinal);

    private static readonly string[] SecretMarkers =
    [
        "password=", "pwd=", "secret=", "apikey=", "api_key=", "accountkey=", "sharedaccesskey=",
        "connectionstring=", "user id=", "private_key", "client_secret", "bearer ",
        "sk-", "ghp_", "xoxb-", "aws_secret",
        // Token shapes that fit inside the 200-char preview when no earlier marker matches:
        // JWTs (three dot-separated base64url segments starting eyJ), Azure SAS query strings,
        // fine-grained GitHub PATs, Slack user/workspace tokens, Google OAuth refresh tokens,
        // and Authorization headers embedded in copied curl commands.
        "eyJ", "sig=", "sv=", "github_pat_", "xoxa-", "xoxp-", "ya29.", "authorization:"
    ];

    private static void AddPrompt(FrameworkContext ctx, FrameworkResults results, string promptText, string filePath, int line)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(promptText)))[..16].ToLowerInvariant();
        var id = FrameworkIds.Ai("prompt", "llm", $"prompt-{hash}");
        if (results.AiComponents.Any(component => component.Id == id))
        {
            return;
        }

        // A 200-character preview is a size cap, not a secret control: most credentials are far
        // shorter than 200 characters, so a misclassified connection string would be emitted in full.
        // Secret-shaped text is withheld entirely, and --include-prompt-text does not override that.
        var secretShaped = LooksLikeSecret(promptText);
        var preview = promptText.Length > 200 ? promptText[..200] : promptText;
        var includeFull = ctx.IncludePromptText && !secretShaped;
        results.AiComponents.Add(new AiComponent
        {
            Id = id,
            Kind = "prompt",
            Name = $"prompt-{hash}",
            Provider = "llm",
            PromptText = secretShaped ? null : includeFull ? promptText : preview,
            Confidence = ConfidenceTiers.Heuristic,
            Location = CodeLocation.From(ctx.BasePath, filePath, line),
            Evidence = new AnalysisEvidence
            {
                Kind = AnalysisEvidenceKind.FrameworkModel,
                Source = "llm",
                Description = secretShaped
                ? $"Candidate prompt withheld: text is secret-shaped (SHA-256 prefix {hash})."
                : includeFull
                    ? "System prompt (full text emitted)."
                    : $"System prompt (SHA-256 prefix {hash}; first 200 chars). Full text requires --include-prompt-text.",
                Confidence = ConfidenceTiers.Heuristic,
                FileName = Path.GetFileName(filePath),
                LineNumber = line
            },
            Properties = { ["sha256"] = hash }
        });
    }

    private static string HostOf(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }
}
