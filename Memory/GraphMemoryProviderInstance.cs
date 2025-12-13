using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Services.Memory;

namespace Voxta.Modules.GraphMemory.Memory;

public class GraphMemoryProviderInstance(
    ILogger logger,
    GraphMemorySettings settings
) : IMemoryProviderInstance
{
    private readonly ILogger _logger = logger;
    private readonly GraphStore _store = new(settings.GraphPath);
    private readonly TextEmbedder _embedder = new();
    private readonly GraphExtractor _graphExtractor = new(logger, settings);
    private Guid? _lastProcessedMessageId;
    private int _maxMemoryTokens;

    public bool Enabled => true;

    public void Configure(int maxMemoryTokens)
    {
        _maxMemoryTokens = maxMemoryTokens;
    }

    public Task RegisterMemoriesAsync(IEnumerable<MemoryRef> items, CancellationToken cancellationToken)
    {
        var lore = items.Select(ToLoreFromMemoryRef).Select(EmbedLore);
        _store.UpsertLore(lore);
        return Task.CompletedTask;
    }

    public Task<bool> PrefillAsync(IReadOnlyList<ChatMessageData> messages, List<CharacterMemoryEntry> characterMemories, CancellationToken cancellationToken)
    {
        if (!settings.PrefillMemoryWindow) return Task.FromResult(false);

        var entries = CollectMemoryRefsForPrefill();
        var expireIndex = GetExpiry(messages);
        var added = PrefillWindow(characterMemories, entries, settings.MaxMemoryWindowEntries, _maxMemoryTokens, expireIndex);
        return Task.FromResult(added);
    }

    public Task<bool> UpdateMemoryWindowAsync(IReadOnlyList<ChatMessageData> messages, List<CharacterMemoryEntry> characterMemories, CancellationToken cancellationToken)
    {
        var newMessages = messages.Since(_lastProcessedMessageId, settings.MaxQueryResults).ToArray();
        if (newMessages.Length == 0) return Task.FromResult(false);

        var terms = ExtractTerms(newMessages);
        var graphMatches = _store.Search(
            terms,
            settings.NeighborLimit,
            settings.MaxHops,
            settings.DeterministicOnly ? null : _embedder,
            settings.MinScore,
            settings.DeterministicOnly);
        var expireIndex = GetExpiry(messages);

        var candidates = RankMatches(graphMatches);
        var changed = InsertCandidates(characterMemories, candidates, expireIndex);

        // Agentic: create a deterministic summary lore from the new messages (placeholder until LLM extraction).
        var summaryLore = SummarizeMessages(newMessages);
        if (summaryLore != null)
        {
            _store.UpsertLore(new[] { EmbedLore(summaryLore) });
        }

        if (settings.EnableGraphExtraction)
        {
            try
            {
                var graphResult = _graphExtractor.Extract(newMessages, _store.Entities, _store.Relations);
                if (graphResult != null)
                {
                    if (graphResult.Entities.Count > 0) _store.UpsertEntities(graphResult.Entities);
                    if (graphResult.Relations.Count > 0) _store.UpsertRelations(graphResult.Relations);
                    if (graphResult.Lore.Count > 0) _store.UpsertLore(graphResult.Lore.Select(EmbedLore));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph extraction failed; skipping graph updates for this batch.");
            }
        }

        _lastProcessedMessageId = newMessages.LastOrDefault()?.LocalId;
        return Task.FromResult(changed);
    }

    public Task<MemorySearchResult[]> SearchAsync(IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var matches = _store.Search(
            values,
            settings.NeighborLimit,
            settings.MaxHops,
            settings.DeterministicOnly ? null : _embedder,
            settings.MinScore,
            settings.DeterministicOnly);
        var ranked = RankMatches(matches);
        var results = ranked.Select((m, i) => new MemorySearchResult { Memory = m, Score = 1.0 - i * 0.01 }).ToArray();
        return Task.FromResult(results);
    }

    public Task UpdateMemoriesAsync(Guid[] remove, MemoryRef[] update, MemoryRef[] add, CancellationToken cancellationToken)
    {
        if (remove.Length > 0) _store.RemoveLore(remove);
        if (update.Length > 0) _store.UpsertLore(update.Select(ToLoreFromMemoryRef).Select(EmbedLore));
        if (add.Length > 0) _store.UpsertLore(add.Select(ToLoreFromMemoryRef).Select(EmbedLore));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private List<MemoryRef> CollectMemoryRefsForPrefill()
    {
        var refs = new List<MemoryRef>();
        refs.AddRange(_store.Entities.Select(GraphMapping.ToMemoryRef));
        refs.AddRange(_store.Relations.Select(r =>
        {
            var src = _store.Entities.FirstOrDefault(e => e.Id == r.SourceId);
            var tgt = _store.Entities.FirstOrDefault(e => e.Id == r.TargetId);
            return GraphMapping.ToMemoryRef(r, src, tgt);
        }));
        refs.AddRange(_store.Lore.Select(GraphMapping.ToMemoryRef));
        return refs;
    }

    private List<MemoryRef> RankMatches(GraphSearchResult matches)
    {
        var ranked = new List<(MemoryRef mem, double score)>();

        foreach (var kv in matches.Lore)
        {
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

        var text = string.Join(" ", messages.Select(m => m.Value).Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(text)) return null;
        var id = DeterministicGuid(text);
        return new GraphLore
        {
            Id = id,
            Text = $"Summary: {text}",
            Keywords = new(),
            Weight = 0,
            Tokens = GraphMapping.EstimateTokens(text),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static Guid DeterministicGuid(string text)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return new Guid(bytes);
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
