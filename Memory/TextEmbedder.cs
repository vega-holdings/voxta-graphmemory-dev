using System.Collections.Concurrent;

namespace Voxta.Modules.GraphMemory.Memory;

/// <summary>
/// Lightweight, dependency-free text embedder using hashed bag-of-words into a fixed vector.
/// This is a placeholder until a proper embedding backend is wired (e.g., MSK SentenceTransformers).
/// </summary>
internal class TextEmbedder
{
    private const int Dimensions = 256;
    private readonly ConcurrentDictionary<string, double[]> _cache = new();

    public double[] Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new double[Dimensions];
        return _cache.GetOrAdd(text, Compute);
    }

    public static double Cosine(double[] a, double[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static double[] Compute(string text)
    {
        var vec = new double[Dimensions];
        var tokens = Tokenize(text);
        foreach (var tok in tokens)
        {
            var h = (uint)tok.GetHashCode();
            var idx = (int)(h % Dimensions);
            vec[idx] += 1;
        }
        return vec;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 2);
}
