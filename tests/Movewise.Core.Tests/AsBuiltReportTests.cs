using Movewise.Core.Reports;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class AsBuiltReportTests
{
    [Fact]
    public void Tenant_names_are_encoded_so_they_cant_add_markup()
    {
        var tenant = new TenantInfo("t1", "</title><script>alert(1)</script>", "a&b.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);

        var html = AsBuiltReport.GenerateHtml(tenant, []);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a&amp;b.onmicrosoft.com", html);
    }
}
