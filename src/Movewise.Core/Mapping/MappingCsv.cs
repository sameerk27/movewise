using System.Text;

namespace Movewise.Core.Mapping;

/// <summary>
/// Saves mappings to a CSV file that can be edited in Excel and loaded again, for example to reuse the
/// group mapping from a pilot run or to prepare it with the customer.
/// </summary>
public static class MappingCsv
{
    static readonly string[] Header = ["Type", "SourceId", "SourceName", "Decision", "DestinationId", "DestinationName"];

    public const string Create = "create";
    public const string Remove = "remove";
    public const string Map = "map";

    public sealed record ImportResult(int Applied, IReadOnlyList<string> Problems);

    public static string Write(MappingPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(",", Header));
        foreach (var mapping in plan.Items.OrderBy(m => MappingNamesIndex(m.TargetType)).ThenBy(m => m.Source.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var decision = mapping.Kind switch
            {
                MatchKind.Unresolved => "",
                MatchKind.CreateInDestination or MatchKind.CreatedByMigration => Create,
                MatchKind.RemoveFromPolicies => Remove,
                _ => Map,
            };
            var destinationId = decision == Map ? mapping.Destination?.Id ?? "" : "";
            var destinationName = decision == Map ? mapping.Destination?.DisplayName ?? "" : "";
            text.AppendLine(string.Join(",", new[] { mapping.TargetType, mapping.Source.Id, mapping.Source.DisplayName, decision, destinationId, destinationName }.Select(Quote)));
        }
        return text.ToString();
    }

    /// <summary>
    /// Applies rows to the plan as manual decisions. Rows for objects the plan doesn't contain, and blank decisions,
    /// are skipped. Destination IDs are checked against the destination during pre-flight.
    /// </summary>
    public static ImportResult Read(MappingPlan plan, string csv)
    {
        var problems = new List<string>();
        var applied = 0;
        var rows = ParseRows(csv).ToList();
        if (rows.Count == 0 || !rows[0].Take(Header.Length).SequenceEqual(Header, StringComparer.OrdinalIgnoreCase))
            return new ImportResult(0, [$"The first row must be the header: {string.Join(",", Header)}"]);

        foreach (var (row, line) in rows.Skip(1).Select((row, index) => (row, index + 2)))
        {
            if (row.All(string.IsNullOrWhiteSpace))
                continue;
            if (row.Count < Header.Length)
            {
                problems.Add($"Line {line}: expected {Header.Length} columns, found {row.Count}.");
                continue;
            }

            var (type, sourceId, decision, destinationId, destinationName) = (row[0].Trim(), row[1].Trim(), row[3].Trim().ToLowerInvariant(), row[4].Trim(), row[5].Trim());
            var mapping = plan.Find(type, sourceId);
            if (mapping is null)
            {
                problems.Add($"Line {line}: {type} {sourceId} isn't used by the selected policies.");
                continue;
            }

            switch (decision)
            {
                case "":
                    continue;
                case Map when destinationId.Length > 0:
                    mapping.MapTo(new ObjectRef(destinationId, destinationName.Length > 0 ? destinationName : destinationId));
                    break;
                case Map:
                    problems.Add($"Line {line}: 'map' needs a DestinationId.");
                    continue;
                case Create when mapping.CanCreate:
                    mapping.CreateInDestination();
                    break;
                case Create:
                    problems.Add($"Line {line}: {MappingNames.Singular(type).ToLowerInvariant()} objects can't be created by Movewise.");
                    continue;
                case Remove:
                    mapping.RemoveFromPolicies();
                    break;
                default:
                    problems.Add($"Line {line}: unknown decision '{decision}'. Use map, create or remove.");
                    continue;
            }
            applied++;
        }

        return new ImportResult(applied, problems);
    }

    static int MappingNamesIndex(string type)
    {
        var index = MappingNames.Order.ToList().IndexOf(type);
        return index < 0 ? int.MaxValue : index;
    }

    static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>RFC 4180 rows: quoted fields may contain commas, quotes ("") and line breaks.</summary>
    static IEnumerable<List<string>> ParseRows(string csv)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
