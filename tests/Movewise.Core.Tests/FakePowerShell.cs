using System.Text.Json.Nodes;
using Movewise.Core.Export;

namespace Movewise.Core.Tests;

/// <summary>
/// Answers cmdlets from canned JSON and records every call. By default a cmdlet accepts any parameter;
/// <see cref="Accepts"/> narrows that, like the real cmdlet's parameter list.
/// </summary>
sealed class FakePowerShell : IPowerShell
{
    readonly Dictionary<string, JsonObject[]> _outputs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _accepts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (Exception Error, int Times)> _failing = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Cmdlet, Dictionary<string, JsonNode?> Parameters)> Calls { get; } = [];

    /// <summary>What the cmdlet returns. Use "Get-X|PolicyName" for rules read one policy at a time.</summary>
    public FakePowerShell Returns(string cmdlet, params string[] items)
    {
        _outputs[cmdlet] = items.Select(i => JsonNode.Parse(i)!.AsObject()).ToArray();
        return this;
    }

    public FakePowerShell Accepts(string cmdlet, params string[] parameters)
    {
        _accepts[cmdlet] = new HashSet<string>(parameters, StringComparer.OrdinalIgnoreCase);
        return this;
    }

    public FakePowerShell Failing(string cmdlet, Exception error, int times = int.MaxValue)
    {
        _failing[cmdlet] = (error, times);
        return this;
    }

    public IEnumerable<Dictionary<string, JsonNode?>> CallsTo(string cmdlet) =>
        Calls.Where(c => c.Cmdlet.Equals(cmdlet, StringComparison.OrdinalIgnoreCase)).Select(c => c.Parameters);

    public Task<IReadOnlyList<JsonObject>> InvokeAsync(string cmdlet, IReadOnlyDictionary<string, JsonNode?>? parameters = null, CancellationToken ct = default)
    {
        var copy = (parameters ?? new Dictionary<string, JsonNode?>()).ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase);
        Calls.Add((cmdlet, copy));

        if (_failing.TryGetValue(cmdlet, out var failure) && failure.Times > 0)
        {
            _failing[cmdlet] = failure with { Times = failure.Times - 1 };
            throw failure.Error;
        }

        var policy = copy.Values.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s is not null);
        var key = _outputs.ContainsKey($"{cmdlet}|{policy}") ? $"{cmdlet}|{policy}" : cmdlet;
        IReadOnlyList<JsonObject> result = _outputs.TryGetValue(key, out var found)
            ? found.Select(o => (JsonObject)o.DeepClone()).ToList()
            : cmdlet.StartsWith("New-", StringComparison.OrdinalIgnoreCase)
                ? [new JsonObject { ["Guid"] = $"guid-{Calls.Count}", ["Name"] = copy.GetValueOrDefault("Name")?.GetValue<string>() }]
                : [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlySet<string>> ParametersOfAsync(string cmdlet, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(_accepts.TryGetValue(cmdlet, out var accepted) ? accepted : new AcceptsAnything());

    sealed class AcceptsAnything : HashSet<string>, IReadOnlySet<string>
    {
        bool IReadOnlySet<string>.Contains(string item) => true;
    }
}
