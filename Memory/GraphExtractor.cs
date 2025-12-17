using System.Text.Json;
using System.Text.RegularExpressions;
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
    private bool _disableLiveTextGen;
    private bool _loggedLiveTextGenUnavailable;

    public GraphExtractor(
        ILogger logger,
        GraphMemorySettings settings,
        IDynamicServiceAccessor<ITextGenService> textGenAccessor)
    {
        _logger = logger;
        _settings = settings;
        _textGenAccessor = textGenAccessor;
    }

    internal bool LiveTextGenAvailable => !_disableLiveTextGen;

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
            if (_disableLiveTextGen) return null;

            if (!TryGetCurrentTextGen(out var service))
            {
                return null;
            }

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

            string? response;
            try
            {
                response = await service.GenerateAsync(request, observer, cancellationToken);
            }
            finally
            {
                observer.Done();
            }

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

    private bool TryGetCurrentTextGen(out ITextGenService service)
    {
        try
        {
            service = _textGenAccessor.GetCurrent();
            return true;
        }
        catch (Exception ex) when (IsChatSessionServicesUnavailable(ex))
        {
            _disableLiveTextGen = true;
            service = null!;

            if (!_loggedLiveTextGenUnavailable)
            {
                _loggedLiveTextGenUnavailable = true;
                _logger.LogWarning(
                    "Graph extraction skipped: chat-scoped TextGen is unavailable (IChatSessionServices not initialized). " +
                    "Set GraphExtractionTrigger=OnlyOnMemoryGeneration and use YOLOLLM (or another summarizer) to write GRAPH_JSON updates to the GraphMemory inbox (Data/GraphMemory/Inbox).");
            }

            return false;
        }
    }

    private static bool IsChatSessionServicesUnavailable(Exception ex)
    {
        if ((ex is NullReferenceException or InvalidOperationException) &&
            ex.Message.Contains("IChatSessionServices", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.InnerException != null && IsChatSessionServicesUnavailable(ex.InnerException);
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

        var prompt = promptTemplate;
        prompt = ReplaceTemplateToken(prompt, "existingEntities", string.Join(", ", names));
        prompt = ReplaceTemplateToken(prompt, "messages", messagesText);
        return prompt;
    }

    private static string ReplaceTemplateToken(string template, string tokenName, string value)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return Regex.Replace(
            template,
            @"\{\{\s*" + Regex.Escape(tokenName) + @"\s*\}\}",
            _ => value ?? string.Empty,
            RegexOptions.CultureInvariant);
    }

    public GraphExtractionResult? TryParseGraphFromText(
        string text,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations)
    {
        return TryParseGraphFromTextContent(text, existingEntities, existingRelations, fallbackScope: null, _logger);
    }

    internal static GraphExtractionResult? TryParseGraphFromTextContent(
        string text,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations,
        GraphScope? fallbackScope,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var idx = text.IndexOf("GRAPH_JSON:", StringComparison.OrdinalIgnoreCase);
        var searchStart = idx >= 0 ? idx : 0;

        var jsonStart = text.IndexOf('{', searchStart);
        if (jsonStart < 0) return null;

        var jsonEnd = text.LastIndexOf('}');
        if (jsonEnd < jsonStart) return null;

        var payload = text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        try
        {
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return ParseGraphJson(doc, existingEntities, existingRelations, fallbackScope);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse GRAPH_JSON payload.");
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
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return ParseGraphJson(doc, existingEntities, existingRelations, fallbackScope);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON response for graph extraction.");
            return null;
        }
    }

    internal static GraphExtractionResult? ParseGraphJson(
        JsonDocument doc,
        IReadOnlyCollection<GraphEntity> existingEntities,
        IReadOnlyCollection<GraphRelation> existingRelations,
        GraphScope? fallbackScope)
    {
        var result = new GraphExtractionResult();
        var scope = ParseScope(doc.RootElement, fallbackScope);

        GraphEntity GetOrCreateEntityByName(string name, string? type = null, string? summary = null, IReadOnlyCollection<string>? aliases = null)
        {
            static bool InScope(GraphEntity e, Guid? chatId)
            {
                if (!chatId.HasValue) return true;
                return !e.ChatId.HasValue || e.ChatId == chatId;
            }

            static bool Matches(GraphEntity e, string candidate)
            {
                if (e.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
                return e.Aliases != null && e.Aliases.Any(a => a.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            }

            var requested = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requested))
            {
                requested = string.Empty;
            }

            var candidates = new List<string>(8);
            if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    var a = (alias ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(a)) candidates.Add(a);
                }
            }

            candidates = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            GraphEntity? FindExisting(IEnumerable<GraphEntity> pool)
            {
                foreach (var e in pool)
                {
                    if (!InScope(e, scope.ChatId)) continue;
                    foreach (var candidate in candidates)
                    {
                        if (Matches(e, candidate))
                        {
                            return e;
                        }
                    }
                }
                return null;
            }

            var existing = FindExisting(result.Entities) ?? FindExisting(existingEntities);
            var id = existing?.Id ?? Guid.NewGuid();
            var finalName = existing?.Name ?? (candidates.Count > 0 ? candidates[0] : requested);

            var mergedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing?.Aliases != null)
            {
                foreach (var a in existing.Aliases)
                {
                    var trimmed = (a ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed)) mergedAliases.Add(trimmed);
                }
            }

            foreach (var candidate in candidates)
            {
                var trimmed = candidate.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (trimmed.Equals(finalName, StringComparison.OrdinalIgnoreCase)) continue;
                mergedAliases.Add(trimmed);
            }

            if (!string.IsNullOrWhiteSpace(requested) && !requested.Equals(finalName, StringComparison.OrdinalIgnoreCase))
            {
                mergedAliases.Add(requested);
            }

            var entity = new GraphEntity
            {
                Id = id,
                Name = finalName,
                Type = string.IsNullOrWhiteSpace(type) ? (existing?.Type ?? "entity") : type!.Trim(),
                Summary = string.IsNullOrWhiteSpace(summary) ? (existing?.Summary ?? string.Empty) : summary!.Trim(),
                Aliases = mergedAliases.ToList(),
                ChatId = scope.ChatId ?? existing?.ChatId,
                SessionId = scope.SessionId ?? existing?.SessionId,
                UserId = scope.UserId ?? existing?.UserId,
                UserName = scope.UserName ?? existing?.UserName,
                CharacterIds = scope.CharacterIds.Count > 0 ? scope.CharacterIds.ToList() : existing?.CharacterIds?.ToList() ?? new List<Guid>(),
                CharacterNames = scope.CharacterNames.Count > 0 ? scope.CharacterNames.ToList() : existing?.CharacterNames?.ToList() ?? new List<string>(),
            };

            result.Entities.RemoveAll(x => x.Id == entity.Id);
            result.Entities.Add(entity);
            return entity;
        }

        static string NormalizeParticipantName(string name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

            var pipe = trimmed.LastIndexOf('|');
            if (pipe >= 0 && pipe + 1 < trimmed.Length)
            {
                var tail = trimmed.Substring(pipe + 1).Trim();
                if (!string.IsNullOrWhiteSpace(tail)) return tail;
            }

            return trimmed;
        }

        void EnsureParticipantEntity(string displayName)
        {
            var full = (displayName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(full)) return;

            var canonical = NormalizeParticipantName(full);
            if (string.IsNullOrWhiteSpace(canonical)) return;

            IReadOnlyCollection<string> aliases = canonical.Equals(full, StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<string>()
                : [full];

            _ = GetOrCreateEntityByName(canonical, type: "Character", summary: null, aliases: aliases);
        }

        if (!string.IsNullOrWhiteSpace(scope.UserName))
        {
            EnsureParticipantEntity(scope.UserName!);
        }
        foreach (var characterName in scope.CharacterNames)
        {
            EnsureParticipantEntity(characterName);
        }

        if (TryGetAnyPropertyIgnoreCase(doc.RootElement, ["entities", "characters"], out var ents) && ents.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in ents.EnumerateArray())
            {
                var name = e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString() ?? string.Empty,
                    JsonValueKind.Object when TryGetPropertyIgnoreCase(e, "name", out var n) => n.GetString() ?? string.Empty,
                    _ => string.Empty
                };
                if (string.IsNullOrWhiteSpace(name)) continue;

                var type = e.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(e, "type", out var t)
                    ? t.GetString() ?? "entity"
                    : "entity";

                var summary = e.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(e, "summary", out var s)
                    ? s.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(summary) &&
                    e.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyIgnoreCase(e, "state", out var state) &&
                    state.ValueKind == JsonValueKind.Object)
                {
                    summary = FormatState(state);
                }

                var aliases = new List<string>();
                if (e.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyIgnoreCase(e, "aliases", out var aliasesEl) &&
                    aliasesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alias in aliasesEl.EnumerateArray())
                    {
                        var value = alias.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) aliases.Add(value!.Trim());
                    }
                }

                _ = GetOrCreateEntityByName(name, type, summary, aliases);
            }
        }

        if (TryGetAnyPropertyIgnoreCase(doc.RootElement, ["relations", "relationships"], out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<(Guid src, Guid tgt, string type)>();
            foreach (var r in rels.EnumerateArray())
            {
                if (!TryGetStringPropertyIgnoreCase(r, "source", out var source) ||
                    !TryGetStringPropertyIgnoreCase(r, "target", out var target) ||
                    string.IsNullOrWhiteSpace(source) ||
                    string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                var relType = TryGetStringPropertyIgnoreCase(r, "relation", out var relationType) ? relationType : null;
                if (string.IsNullOrWhiteSpace(relType))
                {
                    relType = TryGetStringPropertyIgnoreCase(r, "type", out var typeValue) ? typeValue : null;
                }
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(relType))
                    continue;

                var srcEnt = GetOrCreateEntityByName(source);
                var tgtEnt = GetOrCreateEntityByName(target);

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
                    Evidence = TryGetPropertyIgnoreCase(r, "attributes", out var attrs) ? attrs.ToString() : string.Empty,
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

        if (!TryGetPropertyIgnoreCase(root, "meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return scope;
        }

        if (TryGetPropertyIgnoreCase(meta, "chatId", out var chatIdEl) && chatIdEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(chatIdEl.GetString(), out var chatId))
        {
            scope.ChatId = chatId;
        }

        if (TryGetPropertyIgnoreCase(meta, "sessionId", out var sessionIdEl) && sessionIdEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(sessionIdEl.GetString(), out var sessionId))
        {
            scope.SessionId = sessionId;
        }

        if (TryGetPropertyIgnoreCase(meta, "user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(user, "id", out var userIdEl) && userIdEl.ValueKind == JsonValueKind.String &&
                Guid.TryParse(userIdEl.GetString(), out var userId))
            {
                scope.UserId = userId;
            }
            if (TryGetPropertyIgnoreCase(user, "name", out var userNameEl) && userNameEl.ValueKind == JsonValueKind.String)
            {
                scope.UserName = userNameEl.GetString();
            }
        }

        if (TryGetPropertyIgnoreCase(meta, "characters", out var chars) && chars.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<Guid>();
            var names = new List<string>();
            foreach (var c in chars.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                if (TryGetPropertyIgnoreCase(c, "id", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(idEl.GetString(), out var id))
                {
                    ids.Add(id);
                }
                if (TryGetPropertyIgnoreCase(c, "name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
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
        if (TryGetPropertyIgnoreCase(state, "mood", out var mood) && mood.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mood.GetString()))
        {
            parts.Add($"mood={mood.GetString()!.Trim()}");
        }
        if (TryGetPropertyIgnoreCase(state, "status", out var status) && status.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(status.GetString()))
        {
            parts.Add($"status={status.GetString()!.Trim()}");
        }
        if (TryGetPropertyIgnoreCase(state, "goal", out var goal) && goal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(goal.GetString()))
        {
            parts.Add($"goal={goal.GetString()!.Trim()}");
        }
        if (parts.Count > 0) return string.Join(", ", parts);
        return state.ToString();
    }

    private static bool TryGetAnyPropertyIgnoreCase(JsonElement obj, string[] names, out JsonElement value)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(obj, name, out value)) return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetStringPropertyIgnoreCase(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!TryGetPropertyIgnoreCase(obj, name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        return false;
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
