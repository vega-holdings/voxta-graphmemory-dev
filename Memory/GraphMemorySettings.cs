namespace Voxta.Modules.GraphMemory.Memory;

public enum GraphExtractionTrigger
{
    EveryTurn,
    OnlyOnMemoryGeneration,
}

public class GraphMemorySettings
{
    public required string GraphPath { get; init; }
    public required string EmbeddingModel { get; init; }
    public required string ModelsDirectory { get; init; }
    public required string ExtractionPromptPath { get; init; }
    public required string GraphExtractionPromptPath { get; init; }
    public bool EnablePlaceholderExtraction { get; init; }
    public bool EnableGraphExtraction { get; init; }
    public GraphExtractionTrigger GraphExtractionTrigger { get; init; } = GraphExtractionTrigger.OnlyOnMemoryGeneration;
    public bool PrefillMemoryWindow { get; init; }
    public int MaxMemoryWindowEntries { get; init; }
    public int ExpireMemoriesAfter { get; init; }
    public int MaxQueryResults { get; init; }
    public double MinScore { get; init; }
    public int MaxHops { get; init; }
    public int NeighborLimit { get; init; }
    public bool DeterministicOnly { get; init; }
}
