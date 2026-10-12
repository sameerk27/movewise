using System.Text;

namespace Movewise.Core.Compare;

/// <summary>A comparison as CSV, for Excel or the change record: one row per policy or group of settings.</summary>
public static class CompareCsv
{
    public static string Write(ComparisonReport report)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", Quote("Service"), Quote("Type"), Quote("Name"), Quote("Result"), Quote("Settings that differ"), Quote("Notes")));
        foreach (var item in report.Items)
        {
            csv.AppendLine(string.Join(",",
                Quote(item.Type.Service.ToString()),
                Quote(item.Type.DisplayName),
                Quote(item.Name),
                Quote(Describe(report, item.Difference)),
                Quote(string.Join("; ", item.ChangedSettings)),
                Quote(string.Join(" ", item.Notes))));
        }
        return csv.ToString();
    }

    /// <summary>What a result means in words, which depends on whether two tenants or a tenant and its snapshot are compared.</summary>
    public static string Describe(ComparisonReport report, Difference difference) => (difference, report.IsDrift) switch
    {
        (Difference.OnlyInFirst, false) => $"Only in {report.FirstName}",
        (Difference.OnlyInSecond, false) => $"Only in {report.SecondName}",
        (Difference.OnlyInFirst, true) => "Removed since the snapshot",
        (Difference.OnlyInSecond, true) => "Added since the snapshot",
        (Difference.Different, false) => "Different",
        (Difference.Different, true) => "Changed since the snapshot",
        _ => "Same",
    };

    // Formulas are neutralized: a policy name that starts with "=" mustn't run in Excel. A leading tab or carriage
    // return can hide one from that check in some importers, so those are neutralized too.
    static string Quote(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
