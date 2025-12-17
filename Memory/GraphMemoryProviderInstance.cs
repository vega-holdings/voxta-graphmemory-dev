using Microsoft.Extensions.Logging;
using Voxta.Abstractions.DependencyInjection;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Services.Memory;
using Voxta.Abstractions.Services.TextGen;

namespace Voxta.Modules.GraphMemory.Memory;

public class GraphMemoryProviderInstance(
    ILogger logger,
    GraphMemorySettings settings,
    IDynamicServiceAccessor<ITextGenService> textGenAccessor
) : IMemoryProviderInstance
{
    private readonly ILogger _logger = logger;
    private readonly GraphStore _store = GraphStore.GetShared(settings.GraphPath);
    private readonly TextEmbedder _embedder = new();
    private readonly GraphExtractor _graphExtractor = new(logger, settings, textGenAccessor);
    private readonly SemaphoreSlim _graphExtractionGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _loreIdsSync = new();
    private readonly HashSet<Guid> _registeredLoreIds = new();
    private Guid? _lastProcessedMessageId;
    private int _maxMemoryTokens;

    public bool Enabled => true;

    public void Configure(int maxMemoryTokens)
    {
        _maxMemoryTokens = maxMemoryTokens;
    }

    public Task RegisterMemoriesAsync(IEnumerable<MemoryRef> items, CancellationToken cancellationToken)
    {
        foreach (var entry in items)
        {
            ProcessGraphFromMemoryRef(entry);
            if (IsGraphJsonOnly(entry.Text)) continue;
            TrackLoreId(entry.Id);
            var lore = EmbedLore(ToLoreFromMemoryRef(entry));
            _store.UpsertLore(new[] { lore });
        }
        return Task.CompletedTask;
    }

    public Task<bool> PrefillAsync(IReadOnlyList<ChatMessageData> messages, List<CharacterMemoryEntry> characterMemories, CancellationToken cancellationToken)
    {
        if (!settings.PrefillMemoryWindow) return Task.FromResult(false);

        var chatId = messages.FirstOrDefault()?.ChatId;
        if (chatId.HasValue)
        {
            GraphMemoryInbox.IngestForChat(_store, chatId.Value, _logger);
        }
        var entries = CollectMemoryRefsForPrefill(chatId);
        var expireIndex = GetExpiry(messages);
        var added = PrefillWindow(characterMemories, entries, settings.MaxMemoryWindowEntries, _maxMemoryTokens, expireIndex);
        return Task.FromResult(added);
    }

    public Task<bool> UpdateMemoryWindowAsync(IReadOnlyList<ChatMessageData> messages, List<CharacterMemoryEntry> characterMemories, CancellationToken cancellationToken)
    {
        var newMessages = messages.Since(_lastProcessedMessageId, settings.MaxQueryResults).ToArray();
        if (newMessages.Length == 0) return Task.FromResult(false);

        GraphMemoryInbox.IngestForChat(_store, newMessages[0].ChatId, _logger);

        var terms = ExtractTerms(newMessages);
        var graphMatches = _store.Search(
            terms,
            settings.NeighborLimit,
            settings.MaxHops,
            settings.DeterministicOnly ? null : _embedder,
            settings.MinScore,
            settings.DeterministicOnly,
            chatId: newMessages[0].ChatId);
        var expireIndex = GetExpiry(messages);

        var candidates = RankMatches(graphMatches, activeChatId: newMessages[0].ChatId);
        var changed = InsertCandidates(characterMemories, candidates, expireIndex);

        // Agentic: create a deterministic summary lore from the new messages (placeholder until LLM extraction).
        var summaryLore = SummarizeMessages(newMessages);
        if (summaryLore != null)
        {
            _store.UpsertLore(new[] { EmbedLore(summaryLore) });
        }

        QueueGraphExtractionIfNeeded(newMessages, cancellationToken);

        _lastProcessedMessageId = newMessages.LastOrDefault()?.LocalId;
        return Task.FromResult(changed);
    }

    public Task<MemorySearchResult[]> SearchAsync(IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var registeredLoreIds = GetRegisteredLoreIdsSnapshot();
        var matches = _store.Search(
            values,
            settings.NeighborLimit,
            settings.MaxHops,
            settings.DeterministicOnly ? null : _embedder,
            settings.MinScore,
            settings.DeterministicOnly,
            chatId: null);
        var ranked = RankMatches(matches);
        var results = ranked
            .Where(m => registeredLoreIds.Contains(m.Id))
            .Select((m, i) => new MemorySearchResult { Memory = m, Score = 1.0 - i * 0.01 })
            .ToArray();
        return Task.FromResult(results);
    }

    public Task UpdateMemoriesAsync(Guid[] remove, MemoryRef[] update, MemoryRef[] add, CancellationToken cancellationToken)
    {
        if (remove.Length > 0)
        {
            UntrackLoreIds(remove);
            _store.RemoveLore(remove);
        }
        if (update.Length > 0)
        {
            foreach (var entry in update)
            {
                ProcessGraphFromMemoryRef(entry);
                if (IsGraphJsonOnly(entry.Text)) continue;
                TrackLoreId(entry.Id);
                _store.UpsertLore(new[] { EmbedLore(ToLoreFromMemoryRef(entry)) });
            }
        }
        if (add.Length > 0)
        {
            foreach (var entry in add)
            {
                ProcessGraphFromMemoryRef(entry);
                if (IsGraphJsonOnly(entry.Text)) continue;
                TrackLoreId(entry.Id);
                _store.UpsertLore(new[] { EmbedLore(ToLoreFromMemoryRef(entry)) });
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _graphExtractionGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private List<MemoryRef> CollectMemoryRefsForPrefill()
    {
        return CollectMemoryRefsForPrefill(chatId: null);
    }

    private List<MemoryRef> CollectMemoryRefsForPrefill(Guid? chatId)
    {
        static bool InScope(Guid? itemChatId, Guid? desiredChatId)
        {
            if (!desiredChatId.HasValue) return true;
            return !itemChatId.HasValue || itemChatId.Value == desiredChatId.Value;
        }

        var registeredLoreIds = GetRegisteredLoreIdsSnapshot();
        var refs = new List<MemoryRef>();

        refs.AddRange(_store.Entities.Where(e => InScope(e.ChatId, chatId)).Select(GraphMapping.ToMemoryRef));
        refs.AddRange(_store.Relations.Where(r => InScope(r.ChatId, chatId)).Select(r =>
        {
            var src = _store.Entities.FirstOrDefault(e => e.Id == r.SourceId);
            var tgt = _store.Entities.FirstOrDefault(e => e.Id == r.TargetId);
            return GraphMapping.ToMemoryRef(r, src, tgt);
        }));
        refs.AddRange(_store.Lore
            .Where(l => registeredLoreIds.Contains(l.Id) && InScope(l.ChatId, chatId))
            .Select(GraphMapping.ToMemoryRef));
        return refs;
    }

    private List<MemoryRef> RankMatches(GraphSearchResult matches, Guid? activeChatId = null)
    {
        var registeredLoreIds = GetRegisteredLoreIdsSnapshot();
        var ranked = new List<(MemoryRef mem, double score)>();

        foreach (var kv in matches.Lore)
        {
            var lore = kv.Value.lore;
            var visible = registeredLoreIds.Contains(lore.Id) ||
                          (activeChatId.HasValue && lore.ChatId.HasValue && lore.ChatId.Value == activeChatId.Value);
            if (!visible) continue;
            ranked.Add((GraphMapping.ToMemoryRef(kv.Value.lore), kv.Value.score));
        }

        foreach (var kv in matches.Entities)
        {
            ranked.Add((GraphMapping.ToMemoryRef(kv.Value.entity), kv.Value.score));
        }

        foreach (var kv in matches.Relations)
        {
            var rel = kv.Value.relation;
            var src = _store.Entities.FirstOrDefault(e => e.Id == rel.SourceId);
            var tgt = _store.Entities.FirstOrDefault(e => e.Id == rel.TargetId);
            ranked.Add((GraphMapping.ToMemoryRef(rel, src, tgt), kv.Value.score));
        }

        return ranked
            .OrderByDescending(x => x.score)
            .Select(x => x.mem)
            .ToList();
    }

    private bool InsertCandidates(List<CharacterMemoryEntry> characterMemories, List<MemoryRef> candidates, int expireIndex)
    {
        var changed = false;
        var tokenUsed = characterMemories.Sum(m => m.Memory.Tokens);

        foreach (var entry in candidates.Take(settings.MaxQueryResults))
        {
            if (characterMemories.Any(m => m.Memory.Id == entry.Id))
                continue;

            var prospectiveTokens = tokenUsed + entry.Tokens;
            if (_maxMemoryTokens > 0 && prospectiveTokens > _maxMemoryTokens)
                continue;

            characterMemories.Insert(0, new CharacterMemoryEntry(entry, expireIndex));
            tokenUsed += entry.Tokens;
            changed = true;

            while (characterMemories.Count > settings.MaxMemoryWindowEntries)
            {
                characterMemories.RemoveAt(characterMemories.Count - 1);
            }
        }

        return changed;
    }

    private int GetExpiry(IReadOnlyList<ChatMessageData> messages)
    {
        var last = messages.LastOrDefault();
        var conversationIndex = last?.ConversationIndex ?? 0;
        return conversationIndex + settings.ExpireMemoriesAfter + 1;
    }

    private static IEnumerable<string> ExtractTerms(IEnumerable<ChatMessageData> messages)
    {
        return messages
            .SelectMany(m => m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(t => t.Length > 2)
            .Select(t => t.ToLowerInvariant());
    }

    private static GraphLore ToLoreFromMemoryRef(MemoryRef entry)
    {
        return new GraphLore
        {
            Id = entry.Id,
            Text = entry.Text,
            Keywords = entry.Keywords?.ToList() ?? new(),
            Weight = entry.Weight,
            Tokens = entry.Tokens,
            CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private GraphLore EmbedLore(GraphLore lore)
    {
        if (settings.DeterministicOnly) return lore;
        var vec = _embedder.Embed(lore.Text);
        return lore with { Embedding = vec.Select(v => (double)v).ToArray() };
    }

    private GraphLore? SummarizeMessages(IEnumerable<ChatMessageData> messages)
    {
        if (!settings.EnablePlaceholderExtraction)
            return null;

        var first = messages.FirstOrDefault();
        var text = string.Join(" ", messages.Select(m => m.Value).Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(text)) return null;
        var id = DeterministicGuid(text);
        return new GraphLore
        {
            Id = id,
            Text = $"Summary: {text}",
            Keywords = new(),
            ChatId = first?.ChatId,
            UserId = first?.UserId,
            Weight = 0,
            Tokens = GraphMapping.EstimateTokens(text),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void ProcessGraphFromMemoryRef(MemoryRef entry)
    {
        if (!settings.EnableGraphExtraction) return;

        if (IsGraphJsonOnly(entry.Text))
        {
            _logger.LogDebug("GRAPH_JSON memory item received id={Id}:{NewLine}{Text}",
                entry.Id,
                Environment.NewLine,
                entry.Text);
        }

        var parsed = _graphExtractor.TryParseGraphFromText(entry.Text, _store.Entities, _store.Relations);
        if (parsed == null) return;

        if (parsed.Entities.Count > 0 || parsed.Relations.Count > 0)
        {
            _logger.LogInformation("GRAPH_JSON parsed from memory item id={Id}: entities={Entities} relations={Relations}",
                entry.Id, parsed.Entities.Count, parsed.Relations.Count);
        }

        if (parsed.Entities.Count > 0) _store.UpsertEntities(parsed.Entities);
        if (parsed.Relations.Count > 0) _store.UpsertRelations(parsed.Relations);
    }

    private void QueueGraphExtractionIfNeeded(IReadOnlyList<ChatMessageData> newMessages, CancellationToken cancellationToken)
    {
        if (!settings.EnableGraphExtraction) return;
        if (settings.GraphExtractionTrigger != GraphExtractionTrigger.EveryTurn) return;
        if (!_graphExtractor.LiveTextGenAvailable) return;
        if (!_graphExtractionGate.Wait(0)) return;

        _ = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

            try
            {
                _logger.LogDebug("Graph extraction queued (every turn): messages={Count}", newMessages.Count);
                var graphResult = await _graphExtractor.ExtractAsync(newMessages, _store.Entities, _store.Relations, linkedCts.Token);
                if (graphResult != null)
                {
                    if (graphResult.Entities.Count > 0) _store.UpsertEntities(graphResult.Entities);
                    if (graphResult.Relations.Count > 0) _store.UpsertRelations(graphResult.Relations);
                    if (graphResult.Lore.Count > 0) _store.UpsertLore(graphResult.Lore.Select(EmbedLore));

                    _logger.LogInformation("Graph extraction applied: entities={Entities} relations={Relations} lore={Lore}",
                        graphResult.Entities.Count, graphResult.Relations.Count, graphResult.Lore.Count);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph extraction failed; skipping graph updates for this batch.");
            }
            finally
            {
                _graphExtractionGate.Release();
            }
        });
    }

    private static bool IsGraphJsonOnly(string text)
    {
        return text.TrimStart().StartsWith("GRAPH_JSON:", StringComparison.OrdinalIgnoreCase);
    }

    private static Guid DeterministicGuid(string text)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return new Guid(bytes);
    }

    private void TrackLoreId(Guid id)
    {
        lock (_loreIdsSync)
        {
            _registeredLoreIds.Add(id);
        }
    }

    private void UntrackLoreIds(IEnumerable<Guid> ids)
    {
        lock (_loreIdsSync)
        {
            foreach (var id in ids)
            {
                _registeredLoreIds.Remove(id);
            }
        }
    }

    private HashSet<Guid> GetRegisteredLoreIdsSnapshot()
    {
        lock (_loreIdsSync)
        {
            return _registeredLoreIds.Count == 0
                ? new HashSet<Guid>()
                : new HashSet<Guid>(_registeredLoreIds);
        }
    }

    private static bool PrefillWindow(
        List<CharacterMemoryEntry> characterMemories,
        IReadOnlyList<MemoryRef> entries,
        int maxMemoryWindowEntries,
        int maxMemoryTokens,
        int expireConversationIndex)
    {
        var added = false;
        var withCreated = entries.Where(e => e.CreatedAt.HasValue).OrderByDescending(e => e.CreatedAt).Take(maxMemoryWindowEntries);
        var withoutCreated = entries.Where(e => !e.CreatedAt.HasValue).OrderByDescending(e => e.Weight).Take(maxMemoryWindowEntries);
        var ordered = withCreated.Concat(withoutCreated).ToList();

        var tokenCount = 0;
        foreach (var item in ordered)
        {
            if (characterMemories.Count >= maxMemoryWindowEntries)
                break;

            tokenCount += item.Tokens;
            if (maxMemoryTokens > 0 && tokenCount > maxMemoryTokens)
                break;

            characterMemories.Insert(0, new CharacterMemoryEntry(item, expireConversationIndex));
            added = true;
        }
        return added;
    }
}
