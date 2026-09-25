using System.Text;
using Movewise.Core.Preflight;

namespace Movewise.Core.Deploy;

/// <summary>What a run created, failed or skipped, with the new IDs, as CSV for change records.</summary>
public static class DeployReportCsv
{
    public static string Write(DeployRun run)
    {
        var text = new StringBuilder();
        text.AppendLine($"Movewise deployment {run.RunId},{ReportCsv.Quote(run.SourceName)} to {ReportCsv.Quote(run.DestinationName)},{run.Started:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine("Name,Type,Status,DestinationId,SourceId,Message,FollowUp");
        foreach (var step in run.Steps)
        {
            text.AppendLine(string.Join(",",
                ReportCsv.Quote(step.DisplayName),
                ReportCsv.Quote(step.TypeName),
                step.Status.ToString(),
                step.DestinationId ?? "",
                step.SourceId,
                ReportCsv.Quote(step.Message ?? ""),
                ReportCsv.Quote(string.Join(" | ", step.Notes))));
        }
        return text.ToString();
    }
}
