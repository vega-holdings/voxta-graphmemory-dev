using Microsoft.Extensions.Logging;

namespace Voxta.Modules.GraphMemory.Memory;

internal static class GraphMemoryInbox
{
    private const int DefaultMaxFilesPerPass = 64;

    internal static string InboxDirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "GraphMemory", "Inbox");

    internal static int IngestForChat(GraphStore store, Guid chatId, ILogger logger, int maxFiles = DefaultMaxFilesPerPass)
    {
        try
        {
            if (!Directory.Exists(InboxDirectoryPath)) return 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GraphMemory inbox directory check failed.");
            return 0;
        }

        var pattern = $"graph_{chatId:N}_*.txt";

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(InboxDirectoryPath, pattern)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxFiles))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GraphMemory inbox enumeration failed (chatId={ChatId}).", chatId);
            return 0;
        }

        if (files.Length == 0) return 0;

        var processed = 0;
        foreach (var filePath in files)
        {
            var workPath = filePath + ".processing";
            try
            {
                File.Move(filePath, workPath, overwrite: false);
            }
            catch
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(workPath);
                var fallbackScope = new GraphScope { ChatId = chatId };
                var parsed = GraphExtractor.TryParseGraphFromTextContent(text, store.Entities, store.Relations, fallbackScope, logger);
                if (parsed == null)
                {
                    logger.LogWarning("GraphMemory inbox item could not be parsed; moving to Failed. chatId={ChatId} path={Path}",
                        chatId,
                        workPath);
                    MoveToFailed(workPath, logger);
                    continue;
                }

                if (parsed.Entities.Count > 0) store.UpsertEntities(parsed.Entities);
                if (parsed.Relations.Count > 0) store.UpsertRelations(parsed.Relations);
                if (parsed.Lore.Count > 0) store.UpsertLore(parsed.Lore);

                processed++;
                File.Delete(workPath);

                logger.LogInformation("GraphMemory inbox applied chatId={ChatId} entities={Entities} relations={Relations} lore={Lore}",
                    chatId, parsed.Entities.Count, parsed.Relations.Count, parsed.Lore.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GraphMemory inbox processing failed; moving to Failed. chatId={ChatId} path={Path}", chatId, workPath);
                MoveToFailed(workPath, logger);
            }
        }

        return processed;
    }

    internal static int IngestAll(GraphStore store, ILogger logger, int maxFiles = DefaultMaxFilesPerPass)
    {
        try
        {
            if (!Directory.Exists(InboxDirectoryPath)) return 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GraphMemory inbox directory check failed.");
            return 0;
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(InboxDirectoryPath, "graph_*.txt")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxFiles))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GraphMemory inbox enumeration failed.");
            return 0;
        }

        if (files.Length == 0) return 0;

        var chatIds = files
            .Select(TryParseChatIdFromFileName)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();

        var total = 0;
        foreach (var chatId in chatIds)
        {
            total += IngestForChat(store, chatId, logger, maxFiles);
        }

        return total;
    }

    private static Guid? TryParseChatIdFromFileName(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (!name.StartsWith("graph_", StringComparison.OrdinalIgnoreCase)) return null;

        var start = "graph_".Length;
        var underscore = name.IndexOf('_', start);
        if (underscore <= start) return null;

        var chatIdText = name.Substring(start, underscore - start);
        return Guid.TryParseExact(chatIdText, "N", out var chatId) ? chatId : null;
    }

    private static void MoveToFailed(string workPath, ILogger logger)
    {
        try
        {
            var failedDir = Path.Combine(InboxDirectoryPath, "Failed");
            Directory.CreateDirectory(failedDir);

            var name = Path.GetFileName(workPath);
            if (name.EndsWith(".processing", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - ".processing".Length);
            }

            var destPath = Path.Combine(failedDir, name);
            File.Move(workPath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to move GraphMemory inbox item to Failed folder: {Path}", workPath);
        }
    }
}

