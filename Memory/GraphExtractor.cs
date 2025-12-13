using System.Text.Json;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.DependencyInjection;
using Voxta.Abstractions.Services.Memory;
using Voxta.Abstractions.Services.TextGen;
using Voxta.Abstractions.Diagnostics;
using Voxta.Model.Shared;

namespace Voxta.Modules.GraphMemory.Memory;

internal class GraphExtractor
{
    private readonly ILogger _logger;
    private readonly GraphMemorySettings _settings;
    private readonly IDynamicServiceAccessor<ITextGenService> _textGenAccessor;

    public GraphExtractor(
        ILogger logger,
        GraphMemorySettings settings,
        IDynamicServiceAccessor<ITextGenService> textGenAccessor)
    {
        _logger = logger;
        _settings = settings;
        _textGenAccessor = textGenAccessor;
    }

    public async Task<GraphExtractionResult?> ExtractAsync(
        IReadOnlyList<ChatMessageData> messages,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0) return null;

        string prompt;
        try
        {
            prompt = BuildPrompt(messages, existingEntities);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph extraction prompt build failed");
            return null;
        }

        try
        {
            var observer = new InferenceLogger(ServiceTypes.TextGen, "GraphMemory", "GraphExtraction", TimeProvider.System, DisabledPerformanceMetricsTracker.Instance)
            {
                UserId = messages[0].UserId,
                ChatId = messages[0].ChatId,
                MessageId = messages[^1].LocalId,
                Request = new RawDisplayable(prompt),
            };

            var request = new TextGenGenerateRequest
            {
                Messages =
                [
                    new SimpleMessageData { Role = ChatMessageRole.System, Value = prompt },
                    new SimpleMessageData { Role = ChatMessageRole.User, Value = "Return the JSON only." },
                ],
                MaxNewTokens = 768,
            };

            var service = _textGenAccessor.GetCurrent();
            var response = await service.GenerateAsync(request, observer, cancellationToken);
            observer.Done();

            var result = TryParseGraphResponse(response, existingEntities, existingRelations);
            if (result == null)
            {
                _logger.LogDebug("Graph extraction returned no usable entities/relations (len={Length})", response?.Length ?? 0);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph extraction LLM call failed");
            return null;
        }
    }

    private string BuildPrompt(IEnumerable<ChatMessageData> messages, IReadOnlyCollection<GraphEntity> existingEntities)
    {
        var names = existingEntities
            .Select(e => e.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var messagesText = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Value}"));
        var promptTemplate = LoadPrompt();
        return promptTemplate
            .Replace("{{existingEntities}}", string.Join(", ", names))
            .Replace("{{messages}}", messagesText);
    }

    public GraphExtractionResult? TryParseGraphFromText(
        string text,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations)
    {
        // Expect a marker like "GRAPH_JSON:" followed by a JSON object.
        const string marker = "GRAPH_JSON:";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var jsonStart = text.IndexOf('{', idx);
        if (jsonStart < 0) return null;

        var jsonEnd = text.LastIndexOf('}');
        if (jsonEnd < jsonStart) return null;

        var payload = text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return ParseGraphJson(doc, existingEntities, existingRelations);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse GRAPH_JSON block from memory text.");
            return null;
        }
    }

    private GraphExtractionResult? TryParseGraphResponse(
        string? response,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var text = StripCodeFences(response);
        var json = TryExtractJsonObject(text);
        if (json == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseGraphJson(doc, existingEntities, existingRelations);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON response for graph extraction.");
            return null;
        }
    }

    private GraphExtractionResult? ParseGraphJson(
        JsonDocument doc,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations)
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
                if (string.IsNullOrWhiteSpace(summary) && e.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                {
                    summary = FormatState(state);
                }

                var aliases = new List<string>();
                if (e.TryGetProperty("aliases", out var aliasesEl) && aliasesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alias in aliasesEl.EnumerateArray())
                    {
                        var value = alias.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) aliases.Add(value!.Trim());
                    }
                }

                // Dedup by name against existing entities
                var existing = existingEntities.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var id = existing?.Id ?? Guid.NewGuid();
                result.Entities.Add(new GraphEntity
                {
                    Id = id,
                    Name = name,
                    Type = type,
                    Summary = summary,
                    Aliases = aliases,
                });
            }
        }

        if (doc.RootElement.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<(Guid src, Guid tgt, string type)>();
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

                var signature = (srcEnt.Id, tgtEnt.Id, relType);
                if (!seen.Add(signature)) continue;

                var existing = existingRelations.FirstOrDefault(x =>
                    x.SourceId == srcEnt.Id &&
                    x.TargetId == tgtEnt.Id &&
                    x.Type.Equals(relType, StringComparison.OrdinalIgnoreCase));

                result.Relations.Add(new GraphRelation
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
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

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return text;
        var withoutHeader = trimmed.Substring(firstNewline + 1);
        var endFence = withoutHeader.LastIndexOf("```", StringComparison.Ordinal);
        if (endFence < 0) return text;
        return withoutHeader.Substring(0, endFence).Trim();
    }

    private static string? TryExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var end = text.LastIndexOf('}');
        if (end < start) return null;
        return text.Substring(start, end - start + 1);
    }

    private static string FormatState(JsonElement state)
    {
        var parts = new List<string>();
        if (state.TryGetProperty("mood", out var mood) && mood.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mood.GetString()))
        {
            parts.Add($"mood={mood.GetString()!.Trim()}");
        }
        if (state.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(status.GetString()))
        {
            parts.Add($"status={status.GetString()!.Trim()}");
        }
        if (state.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(goal.GetString()))
        {
            parts.Add($"goal={goal.GetString()!.Trim()}");
        }
        if (parts.Count > 0) return string.Join(", ", parts);
        return state.ToString();
    }
}

internal class GraphExtractionResult
{
    public List<GraphEntity> Entities { get; } = new();
    public List<GraphRelation> Relations { get; } = new();
    public List<GraphLore> Lore { get; } = new();
}
