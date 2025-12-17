namespace Voxta.Modules.GraphMemory.Memory;

internal static class GraphMemoryRuntime
{
    private static readonly object Sync = new();
    private static string? _graphPath;
    private static GraphStore? _store;

    internal static GraphStore Initialize(string graphPath)
    {
        lock (Sync)
        {
            _graphPath = graphPath;
            _store = GraphStore.GetShared(graphPath);
            return _store;
        }
    }

    internal static GraphStore GetStoreOrDefault()
    {
        lock (Sync)
        {
            if (_store != null) return _store;
            if (!string.IsNullOrWhiteSpace(_graphPath))
            {
                _store = GraphStore.GetShared(_graphPath);
                return _store;
            }
        }

        return GraphStore.GetShared("graphs/graph-memory.db");
    }

    internal static string? TryGetGraphPath()
    {
        lock (Sync)
        {
            return _graphPath;
        }
    }
}

