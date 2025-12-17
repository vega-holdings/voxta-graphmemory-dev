using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Voxta.Modules.GraphMemory.Memory;

namespace Voxta.Modules.GraphMemory.Controllers;

[ApiController]
[Authorize(Roles = "ADMIN")]
[Route("api/extensions/graph-memory")]
public sealed class GraphMemoryApiController(ILogger<GraphMemoryApiController> logger) : ControllerBase
{
    private readonly ILogger<GraphMemoryApiController> _logger = logger;

    [HttpGet("chats")]
    public ActionResult<ChatsResponse> GetChats()
    {
        var store = GraphMemoryRuntime.GetStoreOrDefault();
        GraphMemoryInbox.IngestAll(store, _logger);

        var entities = store.Entities.Where(e => e.ChatId.HasValue).ToArray();
        var relations = store.Relations.Where(r => r.ChatId.HasValue).ToArray();

        var chatIds = entities
            .Select(e => e.ChatId!.Value)
            .Concat(relations.Select(r => r.ChatId!.Value))
            .Distinct()
            .ToArray();

        var chats = chatIds
            .Select(chatId => BuildChatInfo(chatId, entities, relations))
            .OrderByDescending(c => c.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.Relations)
            .ThenByDescending(c => c.Entities)
            .ToArray();

        return Ok(new ChatsResponse(chats));
    }

    [HttpGet("graph")]
    public ActionResult<GraphResponse> GetGraph(
        [FromQuery] Guid chatId,
        [FromQuery] string? characterName = null)
    {
        if (chatId == Guid.Empty) return BadRequest("chatId is required.");

        var store = GraphMemoryRuntime.GetStoreOrDefault();
        GraphMemoryInbox.IngestForChat(store, chatId, _logger);

        var entities = store.Entities.Where(e => e.ChatId == chatId).ToArray();
        var relations = store.Relations.Where(r => r.ChatId == chatId).ToArray();

        var normalizedCharacterName = string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedCharacterName))
        {
            var names = GetCharacterNameVariants(normalizedCharacterName!);
            entities = entities.Where(e => InCharacterScope(e, names)).ToArray();
            relations = relations.Where(r => InCharacterScope(r, names)).ToArray();
        }

        var nodes = entities
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => new GraphNode(e.Id, e.Name, e.Type, e.Summary, e.Aliases.ToArray(), e.UpdatedAt))
            .ToArray();

        var edges = relations
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => new GraphEdge(r.Id, r.SourceId, r.TargetId, r.Type, r.Evidence, r.UpdatedAt))
            .ToArray();

        var characterNames = entities
            .SelectMany(e => e.CharacterNames ?? [])
            .Concat(relations.SelectMany(r => r.CharacterNames ?? []))
            .Select(n => n.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(new GraphResponse(chatId, normalizedCharacterName, characterNames, nodes, edges));
    }

    [HttpGet("raw")]
    public IActionResult GetRaw(
        [FromQuery] Guid chatId,
        [FromQuery] string? characterName = null)
    {
        if (chatId == Guid.Empty) return BadRequest("chatId is required.");

        var store = GraphMemoryRuntime.GetStoreOrDefault();
        GraphMemoryInbox.IngestForChat(store, chatId, _logger);

        var entities = store.Entities.Where(e => e.ChatId == chatId).ToArray();
        var relations = store.Relations.Where(r => r.ChatId == chatId).ToArray();

        var normalizedCharacterName = string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedCharacterName))
        {
            var names = GetCharacterNameVariants(normalizedCharacterName!);
            entities = entities.Where(e => InCharacterScope(e, names)).ToArray();
            relations = relations.Where(r => InCharacterScope(r, names)).ToArray();
        }

        var payload = new
        {
            chatId,
            characterName = normalizedCharacterName,
            entities,
            relations
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        return Content(json, "application/json");
    }

    public sealed record ChatsResponse(IReadOnlyList<ChatInfo> Chats);

    public sealed record ChatInfo(
        Guid ChatId,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<string> CharacterNames,
        int Entities,
        int Relations);

    public sealed record GraphResponse(
        Guid ChatId,
        string? CharacterName,
        IReadOnlyList<string> CharacterNames,
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges);

    public sealed record GraphNode(
        Guid Id,
        string Name,
        string Type,
        string Summary,
        IReadOnlyList<string> Aliases,
        DateTimeOffset UpdatedAt);

    public sealed record GraphEdge(
        Guid Id,
        Guid SourceId,
        Guid TargetId,
        string Type,
        string Evidence,
        DateTimeOffset UpdatedAt);

    private static ChatInfo BuildChatInfo(Guid chatId, IReadOnlyList<GraphEntity> entities, IReadOnlyList<GraphRelation> relations)
    {
        var ents = entities.Where(e => e.ChatId == chatId).ToArray();
        var rels = relations.Where(r => r.ChatId == chatId).ToArray();

        var updatedAt = ents.Select(e => e.UpdatedAt).Concat(rels.Select(r => r.UpdatedAt)).DefaultIfEmpty().Max();

        var characterNames = ents
            .SelectMany(e => e.CharacterNames ?? [])
            .Concat(rels.SelectMany(r => r.CharacterNames ?? []))
            .Select(n => n.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ChatInfo(chatId, updatedAt == default ? null : updatedAt, characterNames, ents.Length, rels.Length);
    }

    private static IReadOnlyList<string> GetCharacterNameVariants(string name)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());

        var pipe = name.LastIndexOf('|');
        if (pipe >= 0 && pipe + 1 < name.Length)
        {
            var tail = name.Substring(pipe + 1).Trim();
            if (!string.IsNullOrWhiteSpace(tail)) names.Add(tail);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool InCharacterScope(GraphEntity entity, IReadOnlyList<string> characterNames)
    {
        if (entity.CharacterNames == null || entity.CharacterNames.Count == 0) return true;
        return entity.CharacterNames.Any(n => characterNames.Any(cn => n.Equals(cn, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool InCharacterScope(GraphRelation relation, IReadOnlyList<string> characterNames)
    {
        if (relation.CharacterNames == null || relation.CharacterNames.Count == 0) return true;
        return relation.CharacterNames.Any(n => characterNames.Any(cn => n.Equals(cn, StringComparison.OrdinalIgnoreCase)));
    }
}
