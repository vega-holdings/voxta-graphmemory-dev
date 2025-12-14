using System.Text.Json.Serialization;
using Voxta.Abstractions.Services.Memory;

namespace Voxta.Modules.GraphMemory.Memory;

internal record GraphEntity
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("type")] public string Type { get; init; } = "entity";
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("aliases")] public List<string> Aliases { get; init; } = new();
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
    [JsonPropertyName("chatId")] public Guid? ChatId { get; init; }
    [JsonPropertyName("sessionId")] public Guid? SessionId { get; init; }
    [JsonPropertyName("userId")] public Guid? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("characterIds")] public List<Guid> CharacterIds { get; init; } = new();
    [JsonPropertyName("characterNames")] public List<string> CharacterNames { get; init; } = new();
    [JsonPropertyName("weight")] public int Weight { get; init; } = 0;
    [JsonPropertyName("tokens")] public int Tokens { get; init; } = 0;
    [JsonPropertyName("embedding")] public double[]? Embedding { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal record GraphRelation
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("sourceId")] public Guid SourceId { get; init; }
    [JsonPropertyName("targetId")] public Guid TargetId { get; init; }
    [JsonPropertyName("evidence")] public string Evidence { get; init; } = string.Empty;
    [JsonPropertyName("chatId")] public Guid? ChatId { get; init; }
    [JsonPropertyName("sessionId")] public Guid? SessionId { get; init; }
    [JsonPropertyName("userId")] public Guid? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("characterIds")] public List<Guid> CharacterIds { get; init; } = new();
    [JsonPropertyName("characterNames")] public List<string> CharacterNames { get; init; } = new();
    [JsonPropertyName("weight")] public int Weight { get; init; } = 0;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal record GraphLore
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("keywords")] public List<string> Keywords { get; init; } = new();
    [JsonPropertyName("chatId")] public Guid? ChatId { get; init; }
    [JsonPropertyName("sessionId")] public Guid? SessionId { get; init; }
    [JsonPropertyName("userId")] public Guid? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("characterIds")] public List<Guid> CharacterIds { get; init; } = new();
    [JsonPropertyName("characterNames")] public List<string> CharacterNames { get; init; } = new();
    [JsonPropertyName("weight")] public int Weight { get; init; } = 0;
    [JsonPropertyName("tokens")] public int Tokens { get; init; } = 0;
    [JsonPropertyName("embedding")] public double[]? Embedding { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("entityIds")] public List<Guid> EntityIds { get; init; } = new();
    [JsonPropertyName("relationIds")] public List<Guid> RelationIds { get; init; } = new();
}

internal record GraphData
{
    [JsonPropertyName("entities")] public List<GraphEntity> Entities { get; init; } = new();
    [JsonPropertyName("relations")] public List<GraphRelation> Relations { get; init; } = new();
    [JsonPropertyName("lore")] public List<GraphLore> Lore { get; init; } = new();
}

internal static class GraphMapping
{
    public static MemoryRef ToMemoryRef(GraphEntity e) => new()
    {
        Id = e.Id,
        Keywords = e.Aliases.ToArray(),
        Text = string.IsNullOrWhiteSpace(e.Summary) ? e.Name : $"{e.Name}: {e.Summary}",
        Weight = e.Weight,
        Tokens = e.Tokens > 0 ? e.Tokens : EstimateTokens(e.Summary),
        CreatedAt = e.CreatedAt
    };

    public static MemoryRef ToMemoryRef(GraphRelation r, GraphEntity? source, GraphEntity? target)
    {
        var src = source?.Name ?? r.SourceId.ToString();
        var tgt = target?.Name ?? r.TargetId.ToString();
        var text = $"Relation: {src} -[{r.Type}]-> {tgt}. {r.Evidence}".Trim();
        var keywords = new List<string> { r.Type };
        if (source != null) keywords.Add(source.Name);
        if (target != null) keywords.Add(target.Name);
        return new MemoryRef
        {
            Id = r.Id,
            Keywords = keywords.ToArray(),
            Text = text,
            Weight = r.Weight,
            Tokens = EstimateTokens(text),
            CreatedAt = r.CreatedAt
        };
    }

    public static MemoryRef ToMemoryRef(GraphLore l)
    {
        return new MemoryRef
        {
            Id = l.Id,
            Keywords = l.Keywords.ToArray(),
            Text = l.Text,
            Weight = l.Weight,
            Tokens = l.Tokens > 0 ? l.Tokens : EstimateTokens(l.Text),
            CreatedAt = l.CreatedAt
        };
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        // Rough heuristic: 4 chars per token
        return Math.Max(1, text.Length / 4);
    }
}
