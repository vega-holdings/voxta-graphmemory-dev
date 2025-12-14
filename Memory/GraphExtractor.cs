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

            var fallbackScope = new GraphScope
            {
                ChatId = messages[0].ChatId,
                UserId = messages[0].UserId,
            };

            var result = TryParseGraphResponse(response, existingEntities, existingRelations, fallbackScope);
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
            return ParseGraphJson(doc, existingEntities, existingRelations, fallbackScope: null);
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
        IReadOnlyCollection<GraphRelation> existingRelations,
        GraphScope? fallbackScope)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var text = StripCodeFences(response);
        var json = TryExtractJsonObject(text);
        if (json == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseGraphJson(doc, existingEntities, existingRelations, fallbackScope);
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
        IReadOnlyCollection<GraphRelation> existingRelations,
        GraphScope? fallbackScope)
    {
        var result = new GraphExtractionResult();
        var scope = ParseScope(doc.RootElement, fallbackScope);

        GraphEntity GetOrCreateEntityByName(string name, string? type = null, string? summary = null, List<string>? aliases = null)
        {
            var existing = scope.ChatId.HasValue
                ? existingEntities.FirstOrDefault(x =>
                    x.ChatId == scope.ChatId &&
                    x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                : existingEntities.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            var id = existing?.Id ?? Guid.NewGuid();
            return new GraphEntity
            {
                Id = id,
                Name = name,
                Type = string.IsNullOrWhiteSpace(type) ? (existing?.Type ?? "entity") : type!,
                Summary = string.IsNullOrWhiteSpace(summary) ? (existing?.Summary ?? string.Empty) : summary!,
                Aliases = aliases ?? existing?.Aliases?.ToList() ?? new List<string>(),
                ChatId = scope.ChatId ?? existing?.ChatId,
                SessionId = scope.SessionId ?? existing?.SessionId,
                UserId = scope.UserId ?? existing?.UserId,
                UserName = scope.UserName ?? existing?.UserName,
                CharacterIds = scope.CharacterIds.Count > 0 ? scope.CharacterIds.ToList() : existing?.CharacterIds?.ToList() ?? new List<Guid>(),
                CharacterNames = scope.CharacterNames.Count > 0 ? scope.CharacterNames.ToList() : existing?.CharacterNames?.ToList() ?? new List<string>(),
            };
        }

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

                var entity = GetOrCreateEntityByName(name, type, summary, aliases);
                result.Entities.Add(entity);
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
                             GetOrCreateEntityByName(source);

                var tgtEnt = result.Entities.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) ??
                             GetOrCreateEntityByName(target);

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
                    ChatId = scope.ChatId ?? existing?.ChatId,
                    SessionId = scope.SessionId ?? existing?.SessionId,
                    UserId = scope.UserId ?? existing?.UserId,
                    UserName = scope.UserName ?? existing?.UserName,
                    CharacterIds = scope.CharacterIds.Count > 0 ? scope.CharacterIds.ToList() : existing?.CharacterIds?.ToList() ?? new List<Guid>(),
                    CharacterNames = scope.CharacterNames.Count > 0 ? scope.CharacterNames.ToList() : existing?.CharacterNames?.ToList() ?? new List<string>(),
                });
            }
        }

        return result.Entities.Count == 0 && result.Relations.Count == 0 ? null : result;
    }

    private static GraphScope ParseScope(JsonElement root, GraphScope? fallback)
    {
        var scope = fallback ?? new GraphScope();

        if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return scope;
        }

        if (meta.TryGetProperty("chatId", out var chatIdEl) && chatIdEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(chatIdEl.GetString(), out var chatId))
        {
            scope.ChatId = chatId;
        }

        if (meta.TryGetProperty("sessionId", out var sessionIdEl) && sessionIdEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(sessionIdEl.GetString(), out var sessionId))
        {
            scope.SessionId = sessionId;
        }

        if (meta.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            if (user.TryGetProperty("id", out var userIdEl) && userIdEl.ValueKind == JsonValueKind.String &&
                Guid.TryParse(userIdEl.GetString(), out var userId))
            {
                scope.UserId = userId;
            }
            if (user.TryGetProperty("name", out var userNameEl) && userNameEl.ValueKind == JsonValueKind.String)
            {
                scope.UserName = userNameEl.GetString();
            }
        }

        if (meta.TryGetProperty("characters", out var chars) && chars.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<Guid>();
            var names = new List<string>();
            foreach (var c in chars.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                if (c.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(idEl.GetString(), out var id))
                {
                    ids.Add(id);
                }
                if (c.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    var name = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
                }
            }

            if (ids.Count > 0) scope.CharacterIds = ids;
            if (names.Count > 0) scope.CharacterNames = names;
        }

        return scope;
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

internal class GraphScope
{
    public Guid? ChatId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public List<Guid> CharacterIds { get; set; } = new();
    public List<string> CharacterNames { get; set; } = new();
}
