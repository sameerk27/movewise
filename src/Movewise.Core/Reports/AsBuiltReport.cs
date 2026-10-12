using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Tenants;

namespace Movewise.Core.Reports;

public static class AsBuiltReport
{
    // Relaxed escaping keeps apostrophes and accented letters readable; the text is HTML-encoded afterwards anyway.
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string Html(string? text) => System.Web.HttpUtility.HtmlEncode(text ?? "");

    public static string GenerateHtml(TenantInfo tenant, IEnumerable<ExportedResource> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>As-Built Documentation - {Html(tenant.DisplayName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #333; max-width: 1200px; margin: 0 auto; padding: 20px; }");
        sb.AppendLine("h1 { border-bottom: 2px solid #0078D4; padding-bottom: 10px; color: #0078D4; }");
        sb.AppendLine("h2 { margin-top: 2em; border-bottom: 1px solid #eaecef; padding-bottom: 5px; }");
        sb.AppendLine("h3 { margin-top: 1.5em; color: #005a9e; }");
        sb.AppendLine(".policy { background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 4px; padding: 15px; margin-bottom: 20px; }");
        sb.AppendLine("pre { background: #e9ecef; padding: 10px; border-radius: 4px; overflow-x: auto; font-size: 0.9em; }");
        sb.AppendLine(".meta { color: #6c757d; font-size: 0.9em; margin-bottom: 15px; }");
        sb.AppendLine(".dependencies { margin-top: 15px; font-size: 0.9em; }");
        sb.AppendLine(".dependencies ul { margin-top: 5px; padding-left: 20px; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");

        sb.AppendLine($"<h1>As-Built Documentation</h1>");
        sb.AppendLine($"<div class='meta'>Tenant: <strong>{Html(tenant.DisplayName)}</strong> ({Html(tenant.InitialDomain)})<br/>Generated: {Html(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture))}</div>");

        var byType = items.GroupBy(i => i.Type).OrderBy(g => g.Key.Service).ThenBy(g => g.Key.DisplayName);

        foreach (var group in byType)
        {
            sb.AppendLine($"<h2>{Html(group.Key.DisplayName)}</h2>");
            foreach (var item in group.OrderBy(i => i.DisplayName))
            {
                sb.AppendLine($"<div class='policy'>");
                sb.AppendLine($"<h3>{Html(item.DisplayName)}</h3>");
                sb.AppendLine($"<pre><code>{Html(item.Settings.ToJsonString(Indented))}</code></pre>");

                if (item.Dependencies.Count > 0)
                {
                    sb.AppendLine("<div class='dependencies'><strong>Dependencies:</strong><ul>");
                    foreach (var dep in item.Dependencies.OrderBy(d => d.Value))
                    {
                        sb.AppendLine($"<li>{Html(dep.TargetType)}: {Html(dep.Value)} (at <code>{Html(dep.Path)}</code>)</li>");
                    }
                    sb.AppendLine("</ul></div>");
                }

                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
