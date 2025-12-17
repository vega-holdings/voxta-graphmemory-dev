using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxta.Modules.GraphMemory.Memory;

internal class GraphStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GraphStore> SharedStores =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _sync = new();
    private GraphData _data = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GraphStore(string path)
    {
        _path = path;
        Load();
    }

    public static GraphStore GetShared(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return SharedStores.GetOrAdd(fullPath, p => new GraphStore(p));
    }

    public IReadOnlyCollection<GraphEntity> Entities
    {
        get { lock (_sync) return _data.Entities.ToList(); }
    }

    public IReadOnlyCollection<GraphRelation> Relations
    {
        get { lock (_sync) return _data.Relations.ToList(); }
    }

    public IReadOnlyCollection<GraphLore> Lore
    {
        get { lock (_sync) return _data.Lore.ToList(); }
    }

    public void UpsertLore(IEnumerable<GraphLore> items)
    {
        lock (_sync)
        {
            foreach (var l in items)
            {
                var existing = _data.Lore.Find(x => x.Id == l.Id);
                if (existing != null) _data.Lore.Remove(existing);
                _data.Lore.Add(l with { UpdatedAt = DateTimeOffset.UtcNow });
            }
            Save();
        }
    }

    public void RemoveLore(IEnumerable<Guid> ids)
    {
        lock (_sync)
        {
            _data.Lore.RemoveAll(x => ids.Contains(x.Id));
            Save();
        }
    }

    public void UpsertEntities(IEnumerable<GraphEntity> items)
    {
        lock (_sync)
        {
            foreach (var e in items)
            {
                var existing = _data.Entities.Find(x => x.Id == e.Id);
                if (existing != null) _data.Entities.Remove(existing);
                _data.Entities.Add(e with { UpdatedAt = DateTimeOffset.UtcNow });
            }
            Save();
        }
    }

    public void UpsertRelations(IEnumerable<GraphRelation> items)
    {
        lock (_sync)
        {
            foreach (var r in items)
            {
                var existing = _data.Relations.Find(x => x.Id == r.Id);
                if (existing != null) _data.Relations.Remove(existing);
                _data.Relations.Add(r with { UpdatedAt = DateTimeOffset.UtcNow });
            }
            Save();
        }
    }

    public GraphSearchResult Search(
        IEnumerable<string> terms,
        int neighborLimit,
        int maxHops,
        TextEmbedder? embedder,
        double minScore,
        bool deterministicOnly,
        Guid? chatId = null)
    {
        var termList = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.ToLowerInvariant()).ToList();
        if (termList.Count == 0) return new GraphSearchResult();

        lock (_sync)
        {
            var matches = new GraphSearchResult();
            double[]? queryVec = null;
            if (!deterministicOnly && embedder != null)
            {
                queryVec = embedder.Embed(string.Join(' ', termList));
            }

            static bool InScope(Guid? itemChatId, Guid? desiredChatId)
            {
                if (!desiredChatId.HasValue) return true;
                return !itemChatId.HasValue || itemChatId.Value == desiredChatId.Value;
            }

            // Lore match
            foreach (var l in _data.Lore)
            {
                if (!InScope(l.ChatId, chatId)) continue;
                var score = ScoreText(l.Text, l.Keywords, termList);
                if (!deterministicOnly && embedder != null && queryVec != null && l.Embedding != null)
                {
                    var sim = TextEmbedder.Cosine(queryVec, l.Embedding);
                    if (sim >= minScore) score += sim;
                }
                if (score > 0 || (minScore > 0 && score >= minScore))
                {
                    matches.Lore[l.Id] = (l, score);
                }
            }

            // Entity match
            foreach (var e in _data.Entities)
            {
                if (!InScope(e.ChatId, chatId)) continue;
                var score = ScoreText(e.Summary, e.Aliases, termList, e.Name);
                if (!deterministicOnly && embedder != null && queryVec != null && e.Embedding != null)
                {
                    var sim = TextEmbedder.Cosine(queryVec, e.Embedding);
                    if (sim >= minScore) score += sim;
                }
                if (score > 0 || (minScore > 0 && score >= minScore))
                {
                    matches.Entities[e.Id] = (e, score);
                }
            }

            // Relation match
            foreach (var r in _data.Relations)
            {
                if (!InScope(r.ChatId, chatId)) continue;
                var score = ScoreText(r.Evidence, new[] { r.Type }, termList);
                if (score > 0 || (minScore > 0 && score >= minScore))
                {
                    matches.Relations[r.Id] = (r, score);
                }
            }

            // Neighbor expansion
            if (maxHops > 0)
            {
                ExpandNeighbors(matches, neighborLimit, maxHops, chatId);
            }

            return matches;
        }
    }

    private void ExpandNeighbors(GraphSearchResult matches, int neighborLimit, int hops, Guid? chatId)
    {
        var visitedEntities = new HashSet<Guid>(matches.Entities.Keys);
        var visitedRelations = new HashSet<Guid>(matches.Relations.Keys);

        static bool InScope(Guid? itemChatId, Guid? desiredChatId)
        {
            if (!desiredChatId.HasValue) return true;
            return !itemChatId.HasValue || itemChatId.Value == desiredChatId.Value;
        }

        for (int depth = 0; depth < hops; depth++)
        {
            var newEntities = new List<Guid>();
            var newRelations = new List<Guid>();

            foreach (var rel in _data.Relations)
            {
                if (visitedRelations.Contains(rel.Id)) continue;
                if (!InScope(rel.ChatId, chatId)) continue;

                if (visitedEntities.Contains(rel.SourceId) || visitedEntities.Contains(rel.TargetId))
                {
                    matches.Relations[rel.Id] = (rel, 0.1); // low score boost
                    newRelations.Add(rel.Id);

                    var src = rel.SourceId;
                    var tgt = rel.TargetId;
                    if (!visitedEntities.Contains(src) && matches.Entities.Count < neighborLimit)
                    {
                        var ent = _data.Entities.FirstOrDefault(x => x.Id == src);
                        if (ent != null && InScope(ent.ChatId, chatId))
                        {
                            matches.Entities[src] = (ent, 0.05);
                            newEntities.Add(src);
                        }
                    }
                    if (!visitedEntities.Contains(tgt) && matches.Entities.Count < neighborLimit)
                    {
                        var ent = _data.Entities.FirstOrDefault(x => x.Id == tgt);
                        if (ent != null && InScope(ent.ChatId, chatId))
                        {
                            matches.Entities[tgt] = (ent, 0.05);
                            newEntities.Add(tgt);
                        }
                    }
                }
            }

            visitedEntities.UnionWith(newEntities);
            visitedRelations.UnionWith(newRelations);
        }
    }

    private static double ScoreText(string text, IEnumerable<string> keywords, IReadOnlyList<string> terms, string? name = null)
    {
        double score = 0;
        foreach (var term in terms)
        {
            if (!string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1.0;
            if (!string.IsNullOrEmpty(name) && name.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1.0;
            if (keywords != null && keywords.Any(k => k.Contains(term, StringComparison.OrdinalIgnoreCase)))
                score += 0.5;
        }
        return score;
    }

    private void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                EnsureDirectory();
                _data = new GraphData();
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                var items = JsonSerializer.Deserialize<GraphData>(json, JsonOptions);
                _data = items ?? new GraphData();
            }
            catch
            {
                _data = new GraphData();
            }
        }
    }

    private void Save()
    {
        EnsureDirectory();
        var json = JsonSerializer.Serialize(_data, JsonOptions);
        File.WriteAllText(_path, json);
    }

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
    }
}

internal class GraphSearchResult
{
    public Dictionary<Guid, (GraphEntity entity, double score)> Entities { get; } = new();
    public Dictionary<Guid, (GraphRelation relation, double score)> Relations { get; } = new();
    public Dictionary<Guid, (GraphLore lore, double score)> Lore { get; } = new();
}
