using System.Text;
using System.Text.Json;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Services.Memory;
using Microsoft.Extensions.Logging;

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
        // TODO: Wire to an actual LLM/text-gen service. For now, this is a stub that returns null.
        // We keep the plumbing, prompt load, and JSON parse ready for future integration.
        try
        {
            var prompt = BuildPrompt(messages, existingEntities);
            _logger.LogDebug("Graph extraction prompt built (len={Length})", prompt.Length);
            // Placeholder: no LLM call; return null to avoid side effects.
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
        var sb = new StringBuilder();
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
