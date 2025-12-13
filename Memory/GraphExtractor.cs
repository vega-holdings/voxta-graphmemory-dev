using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Services.Memory;

namespace Voxta.Modules.GraphMemory.Memory;

internal class GraphExtractor
{
    private readonly ILogger _logger;
    private readonly GraphMemorySettings _settings;

    public GraphExtractor(ILogger logger, GraphMemorySettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public GraphExtractionResult? Extract(IEnumerable<ChatMessageData> messages, IReadOnlyCollection<GraphEntity> existingEntities, IReadOnlyCollection<GraphRelation> existingRelations)
    {
        // Stub: no LLM wiring available. Keep prompt build and return null.
        try
        {
            var prompt = BuildPrompt(messages, existingEntities);
            _logger.LogDebug("Graph extraction prompt built (len={Length})", prompt.Length);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph extraction prompt build failed");
            return null;
        }
    }

    private string BuildPrompt(IEnumerable<ChatMessageData> messages, IReadOnlyCollection<GraphEntity> existingEntities)
    {
        var names = existingEntities.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
        var messagesText = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Value}"));
        var promptTemplate = LoadPrompt();
        return promptTemplate
            .Replace("{{existingEntities}}", string.Join(", ", names))
            .Replace("{{messages}}", messagesText);
    }

    private string LoadPrompt()
    {
        var path = _settings.GraphExtractionPromptPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Graph extraction prompt not found at {path}");
        }
        return File.ReadAllText(path);
    }
}

internal class GraphExtractionResult
{
    public List<GraphEntity> Entities { get; } = new();
    public List<GraphRelation> Relations { get; } = new();
    public List<GraphLore> Lore { get; } = new();
}
