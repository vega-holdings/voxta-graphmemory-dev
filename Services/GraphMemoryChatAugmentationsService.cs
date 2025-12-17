using System.Text;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Abstractions.Scripting.ActionScripts;
using Voxta.Model.Shared;
using Voxta.Modules.GraphMemory.Configuration;
using Voxta.Modules.GraphMemory.Memory;

namespace Voxta.Modules.GraphMemory.Services;

public class GraphMemoryChatAugmentationsService(
    ILogger<GraphMemoryChatAugmentationsService> logger,
    ILoggerFactory loggerFactory
) : ServiceBase(logger), IChatAugmentationsService
{
    public Task<IChatAugmentationServiceInstanceBase[]> CreateInstanceAsync(
        IChatSessionChatAugmentationApi session,
        IAuthenticationContext auth,
        CancellationToken cancellationToken)
    {
        var graphPath = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.GraphPath);
        var store = GraphMemoryRuntime.Initialize(graphPath);

        IChatAugmentationServiceInstanceBase[] instances =
        [
            new GraphMemoryContextAugmentationInstance(
                session,
                store,
                loggerFactory.CreateLogger<GraphMemoryContextAugmentationInstance>())
        ];
        return Task.FromResult(instances);
    }
}

file sealed class GraphMemoryContextAugmentationInstance : IChatScriptEventsAugmentation
{
    private const string ContextKey = "GraphMemory/ActiveGraph";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    private const int MaxTotalChars = 1800;
    private const int MaxRelations = 20;
    private const int MaxEntities = 20;
    private const int MaxEvidenceChars = 160;
    private const int MaxSummaryChars = 160;

    private readonly IChatSessionChatAugmentationApi _session;
    private readonly GraphStore _store;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationTokenSource _linkedCts;
    private readonly Task _refreshLoop;

    private string? _lastPublishedText;
    private Guid? _lastPublishedCharacterId;
    private Guid? _focusCharacterId;
    private string? _focusCharacterName;

    public GraphMemoryContextAugmentationInstance(
        IChatSessionChatAugmentationApi session,
        GraphStore store,
        ILogger logger)
    {
        _session = session;
        _store = store;
        _logger = logger;

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, session.Abort);
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_linkedCts.Token));
    }

    public ServiceTypes[] GetRequiredServiceTypes() => [];
    public string[] GetAugmentationNames() => [VoxtaModule.AugmentationKey];

    public async Task OnChatScriptEvent(
        IActionScriptEvent e,
        ChatMessageData? message,
        Voxta.Abstractions.Chats.Objects.Characters.ICharacterOrUserData? character,
        CancellationToken cancellationToken)
    {
        if (e is StartActionScriptEvent or GeneratingCompleteActionScriptEvent)
        {
            if (character is { Role: ChatMessageRole.Assistant })
            {
                _focusCharacterId = character.Id;
                _focusCharacterName = character.Name;
            }

            await PublishGraphContextAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.SetContexts(ContextKey, [], CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear GraphMemory context on dispose.");
        }

        _disposeCts.Cancel();
        try
        {
            await _refreshLoop;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GraphMemory context refresh loop ended unexpectedly.");
        }
        finally
        {
            _linkedCts.Dispose();
            _disposeCts.Dispose();
            _publishGate.Dispose();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PublishGraphContextAsync(cancellationToken);
            try
            {
                await Task.Delay(RefreshInterval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
        }
    }

    private async Task PublishGraphContextAsync(CancellationToken cancellationToken)
    {
        if (!await _publishGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var chatId = _session.Chat.ChatId;
            GraphMemoryInbox.IngestForChat(_store, chatId, _logger);

            var focus = GetFocusCharacter();
            var text = BuildGraphContextText(chatId, focus.Id, focus.Name);

            if (text == _lastPublishedText && focus.Id == _lastPublishedCharacterId)
            {
                return;
            }

            ContextDefinition[] contexts = string.IsNullOrWhiteSpace(text)
                ? []
                : [new ContextDefinition { Text = text }];

            await _session.SetContexts(ContextKey, contexts, cancellationToken);

            _lastPublishedText = text;
            _lastPublishedCharacterId = focus.Id;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish GraphMemory context.");
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private (Guid Id, string Name) GetFocusCharacter()
    {
        if (_focusCharacterId.HasValue && !string.IsNullOrWhiteSpace(_focusCharacterName))
        {
            return (_focusCharacterId.Value, _focusCharacterName!);
        }

        return (_session.MainCharacter.Id, _session.MainCharacter.Name);
    }

    private string BuildGraphContextText(Guid chatId, Guid focusCharacterId, string focusCharacterName)
    {
        var activeCharacterNames = GetCharacterNameVariants(focusCharacterName);

        static bool InCharacterScopeEntity(GraphEntity entity, Guid characterId, IReadOnlyList<string> characterNames)
        {
            if ((entity.CharacterIds == null || entity.CharacterIds.Count == 0) &&
                (entity.CharacterNames == null || entity.CharacterNames.Count == 0))
            {
                return true;
            }

            if (entity.CharacterIds?.Contains(characterId) == true) return true;
            if (entity.CharacterNames == null || entity.CharacterNames.Count == 0) return false;

            return entity.CharacterNames.Any(n => characterNames.Any(cn => n.Equals(cn, StringComparison.OrdinalIgnoreCase)));
        }

        static bool InCharacterScopeRelation(GraphRelation relation, Guid characterId, IReadOnlyList<string> characterNames)
        {
            if ((relation.CharacterIds == null || relation.CharacterIds.Count == 0) &&
                (relation.CharacterNames == null || relation.CharacterNames.Count == 0))
            {
                return true;
            }

            if (relation.CharacterIds?.Contains(characterId) == true) return true;
            if (relation.CharacterNames == null || relation.CharacterNames.Count == 0) return false;

            return relation.CharacterNames.Any(n => characterNames.Any(cn => n.Equals(cn, StringComparison.OrdinalIgnoreCase)));
        }

        var entities = _store.Entities
            .Where(e => e.ChatId == chatId)
            .Where(e => InCharacterScopeEntity(e, focusCharacterId, activeCharacterNames))
            .ToArray();

        var relations = _store.Relations
            .Where(r => r.ChatId == chatId)
            .Where(r => InCharacterScopeRelation(r, focusCharacterId, activeCharacterNames))
            .OrderByDescending(r => r.UpdatedAt)
            .ToArray();

        if (entities.Length == 0 && relations.Length == 0)
        {
            return string.Empty;
        }

        var entitiesById = entities
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToDictionary(e => e.Id, e => e);

        var mainEntity = FindEntityForCharacter(entities, activeCharacterNames);
        var selectedRelations = (mainEntity != null
                ? relations.Where(r => r.SourceId == mainEntity.Id || r.TargetId == mainEntity.Id)
                : relations)
            .Take(MaxRelations)
            .ToArray();

        var selectedEntityIds = new HashSet<Guid>();
        foreach (var r in selectedRelations)
        {
            selectedEntityIds.Add(r.SourceId);
            selectedEntityIds.Add(r.TargetId);
        }

        var selectedEntities = entities
            .Where(e => selectedEntityIds.Contains(e.Id))
            .OrderBy(e => e.Name)
            .Take(MaxEntities)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("GraphMemory (relationships)");
        sb.AppendLine($"Main character: {focusCharacterName}");

        if (selectedRelations.Length > 0)
        {
            sb.AppendLine("Relations:");
            foreach (var rel in selectedRelations)
            {
                var src = entitiesById.TryGetValue(rel.SourceId, out var srcEnt) ? srcEnt.Name : rel.SourceId.ToString();
                var tgt = entitiesById.TryGetValue(rel.TargetId, out var tgtEnt) ? tgtEnt.Name : rel.TargetId.ToString();
                var evidence = Truncate(OneLine(rel.Evidence), MaxEvidenceChars);

                var line = $"- {src} -[{rel.Type}]-> {tgt}";
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    line += $" ({evidence})";
                }

                sb.AppendLine(line);
                if (sb.Length >= MaxTotalChars) break;
            }
        }

        if (selectedEntities.Length > 0 && sb.Length < MaxTotalChars)
        {
            sb.AppendLine("Entities:");
            foreach (var ent in selectedEntities)
            {
                var summary = Truncate(OneLine(ent.Summary), MaxSummaryChars);
                var type = OneLine(ent.Type);

                var line = $"- {ent.Name}";
                if (!string.IsNullOrWhiteSpace(type))
                {
                    line += $" [{type}]";
                }
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    line += $": {summary}";
                }

                sb.AppendLine(line);
                if (sb.Length >= MaxTotalChars) break;
            }
        }

        return sb.ToString().TrimEnd();
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

    private static GraphEntity? FindEntityForCharacter(IReadOnlyList<GraphEntity> entities, IReadOnlyList<string> characterNames)
    {
        foreach (var candidate in characterNames)
        {
            var match = entities.FirstOrDefault(e => e.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        foreach (var candidate in characterNames)
        {
            var match = entities.FirstOrDefault(e => candidate.Contains(e.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return null;
    }

    private static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0) return string.Empty;
        if (text.Length <= maxChars) return text;
        return text.Substring(0, Math.Max(0, maxChars - 1)).TrimEnd() + "…";
    }
}
