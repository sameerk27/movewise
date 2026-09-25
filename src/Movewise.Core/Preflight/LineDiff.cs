namespace Movewise.Core.Preflight;

public enum DiffKind { Same, Removed, Added }

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>A line-by-line comparison (longest common subsequence) of two JSON documents, for the before/after view.</summary>
public static class LineDiff
{
    const int MaxLines = 4000;

    public static IReadOnlyList<DiffLine> Compare(string before, string after)
    {
        var a = before.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var b = after.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Very large policies: show both whole rather than spend seconds on the comparison.
        if (a.Length > MaxLines || b.Length > MaxLines)
            return [.. a.Select(l => new DiffLine(DiffKind.Removed, l)), .. b.Select(l => new DiffLine(DiffKind.Added, l))];

        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = a[i] == b[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = new List<DiffLine>(a.Length + b.Length);
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add(new DiffLine(DiffKind.Same, a[x])); x++; y++; }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) result.Add(new DiffLine(DiffKind.Removed, a[x++]));
            else result.Add(new DiffLine(DiffKind.Added, b[y++]));
        }
        while (x < a.Length) result.Add(new DiffLine(DiffKind.Removed, a[x++]));
        while (y < b.Length) result.Add(new DiffLine(DiffKind.Added, b[y++]));
        return result;
    }
}
