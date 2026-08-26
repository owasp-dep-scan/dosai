using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     ML runtimes: ML.NET pipelines and trainers, ONNX inference sessions, TorchSharp,
///     LLamaSharp, and on-disk model artifacts (.onnx/.gguf/.safetensors/.pt) hashed and inventoried
///     as AI components — model files are SBOM components.
/// </summary>
public sealed class MlRuntimeProvider : IFrameworkProvider
{
    private static readonly string[] TrainerCalls = ["Sdca", "SdcaRegression", "FastTree", "LightGbm", "AveragedPerceptron", "Svm", "KMeans", "NaiveBayes", "MatrixFactorization", "FieldAwareFactorizationMachine", "OneHotEncoding"];

    public string Id => "ml-runtime";

    public string DisplayName => "ML runtimes (ML.NET, ONNX, TorchSharp, LLamaSharp)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null || ctx.ModelArtifacts.Count > 0;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "MLContext", "InferenceSession", "TorchSharp", "LLamaSharp", "LLamaWeights", "LoadFromFile", "Model.Load", "Model.Save", "HuggingFace", ".onnx", ".gguf"))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (name is "Load" or "LoadFromFile" or "Save" || name.EndsWith("Session"))
                {
                    var artifact = ProviderHelpers.StringArguments(invocation).FirstOrDefault(IsModelFile);
                    if (artifact is not null)
                    {
                        AddModelArtifact(ctx, results, artifact, tree.FilePath, line, artifact, "local");
                    }
                }

                if (TrainerCalls.Any(trainer => name.Contains(trainer, StringComparison.Ordinal)))
                {
                    AddTrainer(ctx, results, name, tree.FilePath, line);
                }

                // ML.NET trainers are frequently reached as properties: context.SdcaLogisticRegression.
                if (invocation.Expression is MemberAccessExpressionSyntax { Expression: not null } receiver && TrainerCalls.Any(trainer => receiver.Name.Identifier.Text.Contains(trainer, StringComparison.Ordinal)))
                {
                    AddTrainer(ctx, results, receiver.Name.Identifier.Text, tree.FilePath, line);
                }
            }

            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (literal.Token.Value is not string value || !IsModelFile(value))
                {
                    continue;
                }

                AddModelArtifact(ctx, results, value, tree.FilePath, literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1, value, "local");
            }

            // ML.NET trainers reached as properties: context.SdcaLogisticRegression (no invocation).
            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var memberName = memberAccess.Name.Identifier.Text;
                if (TrainerCalls.Any(trainer => memberName.Contains(trainer, StringComparison.Ordinal)))
                {
                    AddTrainer(ctx, results, memberName, tree.FilePath, memberAccess.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }
            }

            // HuggingFace repo ids: "org/model-name" literals near HF APIs.
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (!creation.Type.ToString().Contains("HuggingFace", StringComparison.Ordinal))
                {
                    continue;
                }

                var repo = ProviderHelpers.StringArguments(creation.ArgumentList).FirstOrDefault(value => value.Contains(char.Parse("/"), StringComparison.Ordinal) && !value.Contains(char.Parse(" "), StringComparison.Ordinal));
                if (repo is not null)
                {
                    var id = FrameworkIds.Ai("model", "huggingface", repo.Replace('/', '_'));
                    if (results.AiComponents.Any(component => component.Id == id))
                    {
                        continue;
                    }

                    results.AiComponents.Add(new AiComponent
                    {
                        Id = id,
                        Kind = "model",
                        Name = repo,
                        Provider = "huggingface",
                        Purl = $"pkg:huggingface/{repo.Replace("/", "%2F")}",
                        Deployment = "remote",
                        Confidence = ConfidenceTiers.Syntactic,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath),
                        Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "ml-runtime", Description = $"HuggingFace repository '{repo}'.", Confidence = ConfidenceTiers.Syntactic }
                    });
                }
            }
        }

        // On-disk artifacts not referenced in code still count: they ride along in containers.
        foreach (var artifactPath in ctx.ModelArtifacts)
        {
            var name = Path.GetFileName(artifactPath);
            var id = FrameworkIds.Ai("model", "local", name);
            if (results.AiComponents.Any(component => component.Id == id))
            {
                continue;
            }

            var artifact = Directory.GetFiles(Path.GetDirectoryName(artifactPath)!, name).FirstOrDefault();
            var size = artifact is null ? 0 : new FileInfo(artifact).Length;
            results.AiComponents.Add(new AiComponent
            {
                Id = id,
                Kind = "model",
                Name = name,
                Provider = "local",
                Deployment = "local",
                FilePath = CodeLocation.From(ctx.BasePath, artifactPath).Path,
                Sha256 = HashFile(artifactPath),
                InputFormats = [Path.GetExtension(artifactPath).TrimStart('.').ToLowerInvariant()],
                Confidence = ConfidenceTiers.Heuristic,
                Location = CodeLocation.From(ctx.BasePath, artifactPath),
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "ml-runtime", Description = size > MaxHashableArtifactBytes ? $"On-disk model artifact ({size} bytes); SHA-256 skipped over the {MaxHashableArtifactBytes / (1024 * 1024)} MB cap." : $"On-disk model artifact ({size} bytes).", Confidence = ConfidenceTiers.Heuristic },
                Properties = { ["sizeBytes"] = size.ToString() }
            });
        }
    }

    private static void AddModelArtifact(FrameworkContext ctx, FrameworkResults results, string artifactName, string filePath, int line, string reference, string deployment)
    {
        var id = FrameworkIds.Ai("model", "local", Path.GetFileName(artifactName));
        if (results.AiComponents.Any(component => component.Id == id))
        {
            return;
        }

        var fullpath = ctx.ModelArtifacts.FirstOrDefault(candidate => Path.GetFileName(candidate).Equals(Path.GetFileName(artifactName), StringComparison.OrdinalIgnoreCase));
        results.AiComponents.Add(new AiComponent
        {
            Id = id,
            Kind = "model",
            Name = Path.GetFileName(artifactName),
            Provider = "local",
            Deployment = deployment,
            FilePath = fullpath is null ? artifactName : CodeLocation.From(ctx.BasePath, fullpath).Path,
            Sha256 = fullpath is null ? null : HashFile(fullpath),
            InputFormats = [Path.GetExtension(artifactName).TrimStart('.').ToLowerInvariant()],
            Confidence = fullpath is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath, line),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "ml-runtime", Description = $"Model artifact '{reference}' loaded by code.", Confidence = fullpath is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic, FileName = Path.GetFileName(filePath), LineNumber = line }
        });
    }

    private static void AddTrainer(FrameworkContext ctx, FrameworkResults results, string trainerName, string filePath, int line)
    {
        var id = FrameworkIds.Ai("model", "mlnet", trainerName);
        if (results.AiComponents.Any(component => component.Id == id))
        {
            return;
        }

        results.AiComponents.Add(new AiComponent
        {
            Id = id,
            Kind = "model",
            Name = trainerName,
            Provider = "mlnet",
            Task = TrainerTask(trainerName),
            ArchitectureFamily = TrainerFamily(trainerName),
            Deployment = "local",
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, filePath, line),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "ml-runtime", Description = $"ML.NET trainer '{trainerName}'.", Confidence = ConfidenceTiers.Syntactic, FileName = Path.GetFileName(filePath), LineNumber = line }
        });
    }

    private static string TrainerTask(string trainer) => trainer switch
    {
        _ when trainer.Contains("Regression", StringComparison.Ordinal) || trainer.Contains("FastTree", StringComparison.Ordinal) || trainer.Contains("LightGbm", StringComparison.Ordinal) => "regression",
        _ when trainer.Contains("KMeans", StringComparison.Ordinal) => "clustering",
        _ when trainer.Contains("MatrixFactorization", StringComparison.Ordinal) || trainer.Contains("FactorizationMachine", StringComparison.Ordinal) => "recommendation",
        _ when trainer.Contains("Anomaly", StringComparison.Ordinal) => "anomaly-detection",
        _ => "classification"
    };

    private static string TrainerFamily(string trainer) => trainer switch
    {
        _ when trainer.Contains("Sdca", StringComparison.Ordinal) => "SDCA (linear)",
        _ when trainer.Contains("FastTree", StringComparison.Ordinal) => "GBM",
        _ when trainer.Contains("LightGbm", StringComparison.Ordinal) => "LightGBM",
        _ when trainer.Contains("Svm", StringComparison.Ordinal) => "SVM",
        _ when trainer.Contains("Perceptron", StringComparison.Ordinal) => "perceptron",
        _ when trainer.Contains("KMeans", StringComparison.Ordinal) => "k-means",
        _ => "unknown"
    };

    internal static bool IsModelFile(string value) =>
        value.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".pth", StringComparison.OrdinalIgnoreCase);

    /// <summary>Artifacts larger than this are inventoried without a hash; hashing multi-GB LLM files on every run is not worth the delay.</summary>
    internal const long MaxHashableArtifactBytes = 256L * 1024 * 1024;

    internal static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxHashableArtifactBytes)
            {
                return null;
            }

            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
///     Vector databases: Qdrant, Pinecone, Milvus, Weaviate, Chroma, Redis vector, pgvector, Azure
///     AI Search, Elasticsearch — outbound services with collection/index names and dimensions.
/// </summary>
public sealed class VectorStoreProvider : IFrameworkProvider
{
    public string Id => "vector-store";

    public string DisplayName => "Vector stores (Qdrant, Pinecone, Milvus, pgvector, Azure AI Search, ...)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "QdrantClient", "PineconeClient", "MilvusClient", "Weaviate", "Chroma", "pgvector", "SearchIndexClient", "VectorStore", "CreateCollection", "EmbeddingGenerator"))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            string? storeName = null;
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeName = creation.Type.ToString();
                var provider = ProviderOf(typeName);
                if (provider is null)
                {
                    continue;
                }

                storeName = provider;
                var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var host = ProviderHelpers.StringArguments(creation.ArgumentList).FirstOrDefault(argument => argument.Contains("http", StringComparison.OrdinalIgnoreCase) || argument.Contains("localhost", StringComparison.OrdinalIgnoreCase));
                var service = new ServiceComponent
                {
                    Id = FrameworkIds.Service("vector-store", null, provider),
                    Name = provider,
                    ServiceKind = ServiceKinds.VectorStore,
                    Direction = ServiceDirections.Outbound,
                    Framework = "vector-store",
                    Provider = provider,
                    Confidence = ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, line),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "vector-store", Description = $"{typeName} client construction.", Confidence = ConfidenceTiers.Syntactic, FileName = Path.GetFileName(tree.FilePath), LineNumber = line }
                };
                if (host is not null)
                {
                    service.Endpoints.Add(host);
                }

                results.Services.Add(service);
            }

            // Collection/index creation with dimension metadata.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name is not ("CreateCollection" or "CreateIndex" or "GetCollection" or "Collection") )
                {
                    continue;
                }

                var collection = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                if (collection is null)
                {
                    continue;
                }

                var service = results.Services.FirstOrDefault(candidate => candidate.Framework == "vector-store" && candidate.Name == (storeName ?? "unknown"))
                              ?? new ServiceComponent
                              {
                                  Id = FrameworkIds.Service("vector-store", null, "vector-store"),
                                  Name = "vector-store",
                                  ServiceKind = ServiceKinds.VectorStore,
                                  Direction = ServiceDirections.Outbound,
                                  Framework = "vector-store",
                                  Confidence = ConfidenceTiers.Heuristic,
                                  Location = CodeLocation.From(ctx.BasePath, tree.FilePath)
                              };
                if (service.Properties.TryAdd("collection", collection) && !results.Services.Contains(service))
                {
                    results.Services.Add(service);
                }
            }
        }
    }

    private static string? ProviderOf(string typeName) => typeName switch
    {
        _ when typeName.Contains("Qdrant", StringComparison.Ordinal) => "qdrant",
        _ when typeName.Contains("Pinecone", StringComparison.Ordinal) => "pinecone",
        _ when typeName.Contains("Milvus", StringComparison.Ordinal) => "milvus",
        _ when typeName.Contains("Weaviate", StringComparison.Ordinal) => "weaviate",
        _ when typeName.Contains("Chroma", StringComparison.Ordinal) => "chroma",
        _ when typeName.Contains("Npgsql", StringComparison.Ordinal) => "pgvector",
        _ when typeName.Contains("SearchIndexClient", StringComparison.Ordinal) || typeName.Contains("AzureSearch", StringComparison.Ordinal) => "azure-ai-search",
        _ when typeName.Contains("Elastic", StringComparison.Ordinal) => "elasticsearch",
        _ when typeName.Contains("Redis", StringComparison.Ordinal) => "redis",
        _ => null
    };
}
