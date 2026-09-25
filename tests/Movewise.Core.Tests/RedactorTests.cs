using Movewise.Core.Diagnostics;

namespace Movewise.Core.Tests;

public class RedactorTests
{
    [Fact]
    public void Removes_tokens_and_secrets()
    {
        var text = Redactor.Redact("Authorization: Bearer eyJ0eXAiOiJKV1Qi.eyJhdWQiOiJodHRwczov.sig-part_123 and client_secret=abc123 password: \"hunter2\"");

        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("[hidden]", text);
    }

    [Fact]
    public void Removes_people_and_tenant_names()
    {
        var text = Redactor.Redact("admin@contoso.com signed in to contoso.onmicrosoft.com; site https://contoso-my.sharepoint.com/personal/anna_contoso_com from 203.0.113.7");

        Assert.DoesNotContain("admin@", text);
        Assert.DoesNotContain("contoso.onmicrosoft", text);
        Assert.DoesNotContain("contoso-my.sharepoint", text);
        Assert.DoesNotContain("203.0.113.7", text);
        Assert.Contains("[tenant]-my.sharepoint.com", text);
    }

    [Fact]
    public void Removes_the_tenant_name_from_every_part_of_a_microsoft_domain()
    {
        var text = Redactor.Redact("Routing to contoso.mail.onmicrosoft.com failed");

        Assert.DoesNotContain("contoso", text);
        Assert.Contains("[tenant].onmicrosoft.com", text);
    }

    [Fact]
    public void Removes_onedrive_owners_and_custom_domains()
    {
        var text = Redactor.Redact("Site https://tenant-my.sharepoint.com/personal/anna_contoso_com/Documents isn't there; domain contoso.co.uk isn't verified");

        Assert.DoesNotContain("anna", text);
        Assert.DoesNotContain("contoso", text);
        Assert.Contains("/personal/[user]", text);
        Assert.Contains("[domain]", text);
    }

    [Fact]
    public void Keeps_microsoft_addresses_and_dotnet_names()
    {
        const string text = "System.Net.Http.HttpRequestException: No such host (graph.microsoft.com) at Movewise.Core.Deploy.Deployer.RunAsync";

        Assert.Equal(text, Redactor.Redact(text));
    }

    [Fact]
    public void Removes_ipv6_addresses_and_ranges_but_not_times()
    {
        var text = Redactor.Redact("Named location 2001:db8:85a3::/48 and fe80:0:0:0:200:f8ff:fe21:67cf, checked at 10:45:12");

        Assert.DoesNotContain("2001", text);
        Assert.DoesNotContain("fe80", text);
        Assert.Contains("10:45:12", text);
    }

    [Fact]
    public void Removes_tokens_in_json()
    {
        var text = Redactor.Redact("""{"token_type":"Bearer","access_token":"abc.def-123","refresh_token": "xyz789"}""");

        Assert.DoesNotContain("abc.def-123", text);
        Assert.DoesNotContain("xyz789", text);
    }

    [Fact]
    public void Shortens_ids_so_log_lines_can_still_be_told_apart()
    {
        Assert.Equal("Group 3f2a… not found", Redactor.Redact("Group 3f2a9b10-0000-4000-8000-00000000c7d1 not found"));
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        const string text = "Created 12 policies in 4.2 seconds. Anti-phishing policy \"Execs\" failed: rule priority out of range.";

        Assert.Equal(text, Redactor.Redact(text));
    }
}
