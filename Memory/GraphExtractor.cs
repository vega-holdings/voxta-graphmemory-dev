using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Services.Memory;
using Voxta.Abstractions.Services.TextGen;

namespace Voxta.Modules.GraphMemory.Memory;

internal class GraphExtractor
{
    private readonly ILogger _logger;
    private readonly GraphMemorySettings _settings;
    private readonly IServiceProvider? _services;
    private readonly ITextGenService? _textGen; // Experimental: may resolve if server DI exposes it.

    public GraphExtractor(ILogger logger, IServiceProvider? services, GraphMemorySettings settings)
    {
        _logger = logger;
        _services = services;
        _settings = settings;
        _textGen = _services?.GetService(typeof(ITextGenService)) as ITextGenService;
        if (_textGen == null)
        {
            _logger.LogDebug("GraphExtractor: ITextGenService not resolved; graph extraction will be a no-op.");
        }
        else
        {
            _logger.LogDebug("GraphExtractor: ITextGenService resolved (experimental wiring).");
        }
    }

    public GraphExtractionResult? Extract(IEnumerable<ChatMessageData> messages, IReadOnlyCollection<GraphEntity> existingEntities, IReadOnlyCollection<GraphRelation> existingRelations)
    {
        try
        {
            var prompt = BuildPrompt(messages, existingEntities);
            _logger.LogDebug("Graph extraction prompt built (len={Length})", prompt.Length);

            // Experimental: try to call the resolved ITextGenService if present. This likely won’t work
            // without a module-facing LLM interface (we cannot construct proper chat/character contexts here).
            if (_textGen == null)
            {
                return null;
            }

            var raw = TryGenerateWithTextGen(prompt);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var parsed = JsonDocument.Parse(raw);
            return ParseGraphJson(parsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph extraction prompt build failed");
            return null;
        }
    }

    private string BuildPrompt(IEnumerable<ChatMessageData> messages, IReadOnlyCollection<GraphEntity> existingEntities)
    {
        var sb = new StringBuilder();
        var names = existingEntities.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
        var messagesText = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Value}"));
        var promptTemplate = LoadPrompt();
        return promptTemplate
            .Replace("{{existingEntities}}", string.Join(", ", names))
            .Replace("{{messages}}", messagesText);
    }

    private string? TryGenerateWithTextGen(string prompt)
    {
        try
        {
            // We don’t have a friendly API on ITextGenService for free-form prompts in modules.
            // This is a best-effort hack: attempt to call GenerateReplyAsync with fabricated system/user messages.
            var req = TextGenGenerateRequest.Create("You are a JSON-only graph extractor. Output JSON only.", prompt, null);
            // This signature is not exposed on the interface; use reflection to find a compatible method if any.
            var method = _textGen!.GetType().GetMethod("GenerateReplyAsync", new[] { typeof(TextGenGenerateRequest), typeof(CancellationToken) });
            if (method == null)
            {
                _logger.LogDebug("GraphExtractor: TextGenGenerateRequest overload not found on resolved ITextGenService.");
                return null;
            }

            var taskObj = method.Invoke(_textGen, new object?[] { req, CancellationToken.None });
            if (taskObj is Task<string> task)
            {
                task.Wait();
                return task.Result;
            }

            _logger.LogDebug("GraphExtractor: GenerateReplyAsync did not return Task<string>.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphExtractor: experimental text-gen call failed.");
            return null;
        }
    }

    private GraphExtractionResult? ParseGraphJson(JsonDocument doc)
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
                result.Entities.Add(new GraphEntity
                {
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
                // Resolve or create entity ids
                var srcEnt = result.Entities.FirstOrDefault(x => x.Name == source) ??
                             result.Entities.FirstOrDefault(x => x.Aliases.Contains(source)) ??
                             new GraphEntity { Name = source };
                var tgtEnt = result.Entities.FirstOrDefault(x => x.Name == target) ??
                             result.Entities.FirstOrDefault(x => x.Aliases.Contains(target)) ??
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
