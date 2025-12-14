using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Shared.HuggingFaceUtils;
using Voxta.Modules.GraphMemory.Memory;

namespace Voxta.Modules.GraphMemory.Configuration;

[SuppressMessage("ReSharper", "MemberCanBePrivate.Global", Justification = "Fields are reused in module registration.")]
public class ModuleConfigurationProvider(
    IHuggingFaceModelResolverFactory huggingFaceModelResolverFactory,
    ILogger<ModuleConfigurationProvider> logger
) : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload =>
    [
        GraphPath.Name,
        EmbeddingModel.Name,
        ModelsDirectory.Name,
        ExtractionPromptPath.Name,
        GraphExtractionPromptPath.Name,
        GraphExtractionTrigger.Name,
    ];

    public static readonly FormTextField GraphPath = new()
    {
        Name = "GraphPath",
        Label = "Graph Storage Path",
        Text = "Path to the on-disk graph store file or directory.",
        Placeholder = "graphs/graph-memory.db",
        DefaultValue = "graphs/graph-memory.db",
    };

    public static readonly FormModelField EmbeddingModel = new()
    {
        Name = "EmbeddingModel",
        Label = "Embedding Model",
        Text = "Path or Hugging Face ID for the SentenceTransformers ONNX model (e.g. `hf:Qdrant/all-MiniLM-L6-v2-onnx`).",
        DefaultValue = "hf:Qdrant/all-MiniLM-L6-v2-onnx",
        RepositorySearchHints = new[] { "sentence transformers", "onnx" },
    };

    public static readonly FormTextField ModelsDirectory = HuggingFaceServiceSettingsContributions.GetModelsDirectoryField("SentenceTransformers");

    public static readonly FormTextField ExtractionPromptPath = new()
    {
        Name = "ExtractionPromptPath",
        Label = "Memory Extraction Prompt Path",
        Text = "Path to the Scriban template used for memory extraction.",
        DefaultValue = "Resources/Prompts/Default/en/GraphMemory/MemoryExtractionSystemMessage.graph.scriban",
    };

    public static readonly FormBooleanField EnablePlaceholderExtraction = new()
    {
        Name = "EnablePlaceholderExtraction",
        Label = "Enable Placeholder Extraction",
        Text = "If true, GraphMemory will continue to emit simple placeholder summaries from chat text. Disable to avoid raw chat leaking into the graph until proper LLM-based extraction is wired.",
        DefaultValue = false,
    };

    public static readonly FormTextField GraphExtractionPromptPath = new()
    {
        Name = "GraphExtractionPromptPath",
        Label = "Graph Extraction Prompt Path",
        Text = "Path to the Scriban template used for graph JSON extraction (entities/relations).",
        DefaultValue = "Resources/Prompts/Default/en/GraphMemory/GraphExtraction.graph.scriban",
    };

    public static readonly FormBooleanField EnableGraphExtraction = new()
    {
        Name = "EnableGraphExtraction",
        Label = "Enable Graph Extraction (LLM)",
        Text = "If true, GraphMemory will either parse GRAPH_JSON from memory items or run the graph extraction prompt (depending on GraphExtractionTrigger).",
        DefaultValue = true,
    };

    public static readonly FormEnumField<GraphExtractionTrigger> GraphExtractionTrigger = new()
    {
        Name = "GraphExtractionTrigger",
        Label = "Graph Extraction Trigger",
        Text = "Run graph extraction every turn (costlier) or only when memories are generated (requires GRAPH_JSON in memory items, e.g. from YOLOLLM graph extraction).",
        DefaultValue = Memory.GraphExtractionTrigger.OnlyOnMemoryGeneration,
        Choices =
        [
            new FormEnumField<GraphExtractionTrigger>.Choice { Label = "Every turn", Value = Memory.GraphExtractionTrigger.EveryTurn },
            new FormEnumField<GraphExtractionTrigger>.Choice { Label = "Only on memory generation", Value = Memory.GraphExtractionTrigger.OnlyOnMemoryGeneration },
        ]
    };

    public static readonly FormBooleanField PrefillMemoryWindow = new()
    {
        Name = "PrefillMemoryWindow",
        Label = "Prefill Memory Window",
        Text = "If enabled, seeds the window with top graph lore on chat start.",
        DefaultValue = true,
    };

    public static readonly FormIntSliderField MaxMemoryWindowEntries = new()
    {
        Name = "MaxMemoryWindowEntries",
        Label = "Max Memory Window Entries",
        Text = "Maximum active memory entries.",
        DefaultValue = 12,
        Min = 1,
        Max = 100,
        SoftMin = 5,
        SoftMax = 25,
    };

    public static readonly FormIntSliderField ExpireMemoriesAfter = new()
    {
        Name = "ExpireMemoriesAfter",
        Label = "Expire Memories After",
        Text = "Messages after which active memories expire (0 = never).",
        DefaultValue = 8,
        Min = 0,
        Max = 50,
        SoftMin = 0,
        SoftMax = 15,
        InactiveValue = 0,
    };

    public static readonly FormIntSliderField MaxQueryResults = new()
    {
        Name = "MaxQueryResults",
        Label = "Max Query Results",
        Text = "Cap on retrieved graph candidates per update.",
        DefaultValue = 16,
        Min = 1,
        Max = 100,
        SoftMin = 5,
        SoftMax = 20,
    };

    public static readonly FormDoubleSliderField MinScore = new()
    {
        Name = "MinScore",
        Label = "Minimum Similarity Score",
        Text = "Minimum embedding similarity to include a match (0 disables).",
        DefaultValue = 0.25,
        Min = 0,
        Max = 1,
        SoftMin = 0.1,
        SoftMax = 0.8,
        Precision = 2,
        InactiveValue = 0,
    };

    public static readonly FormIntSliderField MaxHops = new()
    {
        Name = "MaxHops",
        Label = "Max Graph Hops",
        Text = "How many hops of neighbors to include (0 = direct only).",
        DefaultValue = 1,
        Min = 0,
        Max = 2,
        SoftMin = 0,
        SoftMax = 2,
    };

    public static readonly FormIntSliderField NeighborLimit = new()
    {
        Name = "NeighborLimit",
        Label = "Neighbor Limit",
        Text = "Cap on neighbors pulled per matched entity/relation.",
        DefaultValue = 5,
        Min = 1,
        Max = 50,
        SoftMin = 2,
        SoftMax = 15,
    };

    public static readonly FormBooleanField DeterministicOnly = new()
    {
        Name = "DeterministicOnly",
        Label = "Deterministic (Keyword-Only) Retrieval",
        Text = "If true, skip embeddings and use keywords/text matching only.",
        DefaultValue = false,
    };

    public async Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken
    )
    {
        var populatedEmbedding = await HuggingFaceServiceSettingsContributions.PopulateAsync(
            huggingFaceModelResolverFactory.Create(auth, settings.GetRequired(ModelsDirectory)),
            EmbeddingModel,
            (string modelsPath) =>
            {
                var options = Directory.Exists(modelsPath)
                    ? Directory.GetDirectories(modelsPath)
                        .Where(x => x.Contains("onnx", StringComparison.InvariantCultureIgnoreCase))
                        .ToList()
                    : new List<string>();
                return Task.FromResult<IReadOnlyList<string>>(options);
            },
            logger);

        return FormBuilder.Build(
            FormTitleField.Create("Graph Memory", null, false),
            GraphPath,
            populatedEmbedding,
            ModelsDirectory,
            PrefillMemoryWindow,
            MaxMemoryWindowEntries,
            ExpireMemoriesAfter,
            MaxQueryResults,
            MinScore,
            MaxHops,
            NeighborLimit,
            DeterministicOnly,
            FormTitleField.Create("Extraction", null, false),
            ExtractionPromptPath,
            EnablePlaceholderExtraction,
            GraphExtractionPromptPath,
            EnableGraphExtraction,
            GraphExtractionTrigger.AsField()
        );
    }
}
