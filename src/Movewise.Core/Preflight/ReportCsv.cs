using System.Text;

namespace Movewise.Core.Preflight;

/// <summary>The pre-flight report as CSV: every finding and every policy's outcome, for change records.</summary>
public static class ReportCsv
{
    public static string Write(PreflightReport report, string source, string destination)
    {
        var text = new StringBuilder();
        text.AppendLine($"Movewise pre-flight,{Quote(source)} to {Quote(destination)},{DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine("Severity,Finding,Detail,Item");
        foreach (var finding in report.Findings)
        {
            IReadOnlyList<string> items = finding.Items.Count == 0 ? [""] : finding.Items;
            foreach (var item in items)
                text.AppendLine(string.Join(",", finding.Severity.ToString(), Quote(finding.Title), Quote(finding.Detail), Quote(item)));
        }

        text.AppendLine();
        text.AppendLine("Policy,Type,Outcome,Reasons,Changes");
        foreach (var policy in report.Policies)
        {
            text.AppendLine(string.Join(",",
                Quote(policy.Source.DisplayName),
                Quote(policy.Source.Type.DisplayName),
                policy.Outcome.ToString(),
                Quote(string.Join(" | ", policy.Reasons)),
                Quote(string.Join(" | ", policy.Policy.Changes.Select(c => c.Description)))));
        }
        return text.ToString();
    }

    internal static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
