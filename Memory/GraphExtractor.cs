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

    public GraphExtractionResult? TryParseGraphFromText(string text, IReadOnlyCollection<GraphEntity> existingEntities)
    {
        // Expect a marker like "GRAPH_JSON:" followed by a JSON object.
        const string marker = "GRAPH_JSON:";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var jsonStart = text.IndexOf('{', idx);
        if (jsonStart < 0) return null;

        var payload = text.Substring(jsonStart);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return ParseGraphJson(doc, existingEntities);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse GRAPH_JSON block from memory text.");
            return null;
        }
    }

    private GraphExtractionResult? ParseGraphJson(JsonDocument doc, IReadOnlyCollection<GraphEntity> existingEntities)
    {
        var result = new GraphExtractionResult();

        if (doc.RootElement.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in ents.EnumerateArray())
            {
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "entity" : "entity";
                var summary = e.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                // Dedup by name against existing entities
                var existing = existingEntities.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var id = existing?.Id ?? Guid.NewGuid();
                result.Entities.Add(new GraphEntity
                {
                    Id = id,
                    Name = name,
                    Type = type,
                    Summary = summary,
                    Aliases = new List<string>(),
                });
            }
        }

        if (doc.RootElement.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rels.EnumerateArray())
            {
                var source = r.TryGetProperty("source", out var s) ? s.GetString() : null;
                var target = r.TryGetProperty("target", out var t) ? t.GetString() : null;
                var relType = r.TryGetProperty("relation", out var rt) ? rt.GetString() : null;
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(relType))
                    continue;

                var srcEnt = result.Entities.FirstOrDefault(x => x.Name.Equals(source, StringComparison.OrdinalIgnoreCase)) ??
                             existingEntities.FirstOrDefault(x => x.Name.Equals(source, StringComparison.OrdinalIgnoreCase)) ??
                             new GraphEntity { Name = source };

                var tgtEnt = result.Entities.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) ??
                             existingEntities.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) ??
                             new GraphEntity { Name = target };

                if (!result.Entities.Contains(srcEnt)) result.Entities.Add(srcEnt);
                if (!result.Entities.Contains(tgtEnt)) result.Entities.Add(tgtEnt);

                result.Relations.Add(new GraphRelation
                {
                    SourceId = srcEnt.Id,
                    TargetId = tgtEnt.Id,
                    Type = relType,
                    Evidence = r.TryGetProperty("attributes", out var attrs) ? attrs.ToString() : string.Empty,
                });
            }
        }

        return result.Entities.Count == 0 && result.Relations.Count == 0 ? null : result;
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
