using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Registry;

namespace Movewise.Core.Tests;

public class MatcherTests
{
    const string SourceGroup = "aaaaaaaa-0000-4000-8000-000000000001";
    const string DestinationGroup = "bbbbbbbb-0000-4000-8000-000000000001";
    const string SourceUser = "aaaaaaaa-0000-4000-8000-000000000002";
    const string DestinationUser = "bbbbbbbb-0000-4000-8000-000000000002";
    const string SharePointAppId = "00000003-0000-0ff1-ce00-000000000000";
    const string CustomAppId = "cccccccc-0000-4000-8000-000000000003";
    const string SourceLocation = "aaaaaaaa-0000-4000-8000-000000000004";

    const string GroupSelect = "id,displayName,mailNickname,description,groupTypes,securityEnabled,mailEnabled,membershipRule,membershipRuleProcessingState";
    const string UserSelect = "id,displayName,userPrincipalName,mail";

    static string Page(params string[] items) => $$"""{ "value": [ {{string.Join(",", items)}} ] }""";

    static ExportedResource Policy(string name, params Dependency[] dependencies) =>
        new(ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy), $"policy-{name}", name, new JsonObject(), dependencies);

    static Dependency On(string type, string id) => new(type, id, "test");

    static FakeGraph SourceWithGroup(string name, string nickname) => new FakeGraph()
        .Object(GraphQuery.Item("v1.0/groups", SourceGroup, GroupSelect),
            $$"""{ "id": "{{SourceGroup}}", "displayName": "{{name}}", "mailNickname": "{{nickname}}", "securityEnabled": true }""");

    static string GroupsWhere(string filter) => GraphQuery.Where("v1.0/groups", filter, "id,displayName,mailNickname", top: 5);

    [Fact]
    public async Task Group_matches_by_name()
    {
        var destination = new FakeGraph()
            .Object(GroupsWhere("displayName eq 'All Staff'"), Page($$"""{ "id": "{{DestinationGroup}}", "displayName": "All Staff" }"""));
        var policy = Policy("CA001", On(ResourceRegistry.Group, SourceGroup));

        var plan = await Matcher.BuildAsync(SourceWithGroup("All Staff", "allstaff"), destination, [policy], [policy]);

        var mapping = plan.Find(ResourceRegistry.Group, SourceGroup)!;
        Assert.Equal(MatchKind.SameName, mapping.Kind);
        Assert.Equal(DestinationGroup, mapping.Destination!.Id);
        Assert.Equal(new[] { "CA001" }, mapping.UsedBy);
    }

    [Fact]
    public async Task Group_falls_back_to_mail_nickname()
    {
        var destination = new FakeGraph()
            .Object(GroupsWhere("displayName eq 'SG-Finance'"), Page())
            .Object(GroupsWhere("mailNickname eq 'finance'"), Page($$"""{ "id": "{{DestinationGroup}}", "displayName": "Finance Team", "mailNickname": "finance" }"""));
        var policy = Policy("CA002", On(ResourceRegistry.Group, SourceGroup));

        var plan = await Matcher.BuildAsync(SourceWithGroup("SG-Finance", "finance"), destination, [policy], [policy]);

        var mapping = plan.Find(ResourceRegistry.Group, SourceGroup)!;
        Assert.Equal(MatchKind.SameMailNickname, mapping.Kind);
        Assert.Equal("Finance Team", mapping.Destination!.DisplayName);
    }

    [Fact]
    public async Task Two_destination_groups_with_the_same_name_are_left_for_the_admin()
    {
        var destination = new FakeGraph()
            .Object(GroupsWhere("displayName eq 'Executives'"), Page("""{ "id": "1", "displayName": "Executives" }""", """{ "id": "2", "displayName": "Executives" }"""))
            .Object(GroupsWhere("mailNickname eq 'execs'"), Page());
        var policy = Policy("CA003", On(ResourceRegistry.Group, SourceGroup));

        var plan = await Matcher.BuildAsync(SourceWithGroup("Executives", "execs"), destination, [policy], [policy]);

        var mapping = plan.Find(ResourceRegistry.Group, SourceGroup)!;
        Assert.Equal(MatchKind.Unresolved, mapping.Kind);
        Assert.Contains("2 groups", mapping.Note);
        Assert.True(mapping.CanCreate);
    }

    [Fact]
    public async Task Names_with_quotes_are_escaped()
    {
        var destination = new FakeGraph()
            .Object(GroupsWhere("displayName eq 'O''Brien team'"), Page($$"""{ "id": "{{DestinationGroup}}", "displayName": "O'Brien team" }"""));
        var policy = Policy("CA004", On(ResourceRegistry.Group, SourceGroup));

        var plan = await Matcher.BuildAsync(SourceWithGroup("O'Brien team", ""), destination, [policy], [policy]);

        Assert.Equal(MatchKind.SameName, plan.Find(ResourceRegistry.Group, SourceGroup)!.Kind);
    }

    [Fact]
    public async Task A_group_deleted_in_the_source_is_unresolved_and_cannot_be_created()
    {
        var policy = Policy("CA005", On(ResourceRegistry.Group, SourceGroup));
        var source = new FakeGraph().Failing(GraphQuery.Item("v1.0/groups", SourceGroup, GroupSelect)).Object(GraphQuery.Item("v1.0/groups", SourceGroup, GroupSelect), "{}");

        var plan = await Matcher.BuildAsync(source, new FakeGraph(), [policy], [policy]);

        var mapping = plan.Find(ResourceRegistry.Group, SourceGroup)!;
        Assert.Equal(MatchKind.Unresolved, mapping.Kind);
        Assert.False(mapping.CanCreate);
        Assert.Contains("deleted", mapping.Note);
    }

    [Fact]
    public async Task User_matches_by_email_then_username()
    {
        var source = new FakeGraph().Object(GraphQuery.Item("v1.0/users", SourceUser, UserSelect),
            $$"""{ "id": "{{SourceUser}}", "displayName": "Ana Diaz", "userPrincipalName": "ana.diaz@contoso.com", "mail": "" }""");
        var destination = new FakeGraph().Object(
            GraphQuery.Where("v1.0/users", "startswith(userPrincipalName,'ana.diaz@')", UserSelect, top: 5),
            Page($$"""{ "id": "{{DestinationUser}}", "displayName": "Ana Diaz", "userPrincipalName": "ana.diaz@fabrikam.com" }"""));
        var policy = Policy("CA006", On(ResourceRegistry.User, SourceUser));

        var plan = await Matcher.BuildAsync(source, destination, [policy], [policy]);

        var mapping = plan.Find(ResourceRegistry.User, SourceUser)!;
        Assert.Equal(MatchKind.SameUsername, mapping.Kind);
        Assert.Equal("ana.diaz@fabrikam.com", mapping.Destination!.Detail);
        Assert.False(mapping.CanCreate);
    }

    [Fact]
    public async Task The_same_username_with_a_different_name_is_left_for_the_admin()
    {
        var source = new FakeGraph().Object(GraphQuery.Item("v1.0/users", SourceUser, UserSelect),
            $$"""{ "id": "{{SourceUser}}", "displayName": "John Smith", "userPrincipalName": "john@contoso.com", "mail": "" }""");
        var destination = new FakeGraph().Object(
            GraphQuery.Where("v1.0/users", "startswith(userPrincipalName,'john@')", UserSelect, top: 5),
            Page($$"""{ "id": "{{DestinationUser}}", "displayName": "John Doe", "userPrincipalName": "john@fabrikam.com" }"""));
        var policy = Policy("CA009", On(ResourceRegistry.User, SourceUser));

        var plan = await Matcher.BuildAsync(source, destination, [policy], [policy]);

        var mapping = plan.Find(ResourceRegistry.User, SourceUser)!;
        Assert.Equal(MatchKind.Unresolved, mapping.Kind);
        Assert.Contains("john@fabrikam.com", mapping.Note);
        Assert.Contains("John Doe", mapping.Note);
    }

    [Fact]
    public async Task Apps_match_when_the_destination_has_the_same_app_id()
    {
        string AppsWhere(string appId) => GraphQuery.Where("v1.0/servicePrincipals", $"appId eq '{appId}'", "id,appId,displayName", top: 5);
        var destination = new FakeGraph()
            .Object(AppsWhere(SharePointAppId), Page($$"""{ "id": "x", "appId": "{{SharePointAppId}}", "displayName": "Office 365 SharePoint Online" }"""))
            .Object(AppsWhere(CustomAppId), Page());
        var source = new FakeGraph()
            .Object(AppsWhere(CustomAppId), Page($$"""{ "id": "y", "appId": "{{CustomAppId}}", "displayName": "Contoso HR portal" }"""));
        var policy = Policy("CA007", On(ResourceRegistry.Application, SharePointAppId), On(ResourceRegistry.Application, CustomAppId));

        var plan = await Matcher.BuildAsync(source, destination, [policy], [policy]);

        Assert.Equal(MatchKind.SameAppId, plan.Find(ResourceRegistry.Application, SharePointAppId)!.Kind);
        var custom = plan.Find(ResourceRegistry.Application, CustomAppId)!;
        Assert.Equal(MatchKind.Unresolved, custom.Kind);
        Assert.Equal("Contoso HR portal", custom.Source.DisplayName);
    }

    [Fact]
    public async Task Selected_locations_are_migrated_with_the_policies_unless_one_already_exists()
    {
        var namedLocations = ResourceRegistry.Get(ResourceRegistry.NamedLocation);
        var hq = new ExportedResource(namedLocations, SourceLocation, "HQ London", new JsonObject(), []);
        var branch = new ExportedResource(namedLocations, "branch", "Branch Paris", new JsonObject(), []);
        var policy = Policy("CA008", On(ResourceRegistry.NamedLocation, SourceLocation), On(ResourceRegistry.NamedLocation, "branch"));
        var destination = new FakeGraph().Collection(namedLocations.ListPath, """{ "id": "dest-paris", "displayName": "branch paris" }""");

        var plan = await Matcher.BuildAsync(new FakeGraph(), destination, [policy, hq], [policy, hq, branch]);

        Assert.Equal(MatchKind.CreatedByMigration, plan.Find(ResourceRegistry.NamedLocation, SourceLocation)!.Kind);
        var paris = plan.Find(ResourceRegistry.NamedLocation, "branch")!;
        Assert.Equal(MatchKind.SameName, paris.Kind);
        Assert.Equal("dest-paris", paris.Destination!.Id);
    }

    [Fact]
    public async Task Manual_decisions_survive_matching_again()
    {
        var destination = new FakeGraph().Object(GroupsWhere("displayName eq 'Pilot'"), Page()).Object(GroupsWhere("mailNickname eq 'pilot'"), Page());
        var source = SourceWithGroup("Pilot", "pilot");
        var first = Policy("CA009", On(ResourceRegistry.Group, SourceGroup));
        var plan = await Matcher.BuildAsync(source, destination, [first], [first]);
        plan.Find(ResourceRegistry.Group, SourceGroup)!.MapTo(new ObjectRef(DestinationGroup, "Intune pilot devices"));

        var second = Policy("CA010", On(ResourceRegistry.Group, SourceGroup));
        var again = await Matcher.BuildAsync(source, destination, [first, second], [first, second], previous: plan);

        var mapping = again.Find(ResourceRegistry.Group, SourceGroup)!;
        Assert.Equal(MatchKind.Manual, mapping.Kind);
        Assert.Equal(DestinationGroup, mapping.Destination!.Id);
        Assert.Equal(new[] { "CA009", "CA010" }, mapping.UsedBy);
    }

    [Fact]
    public async Task Csv_round_trip_keeps_decisions()
    {
        var destination = new FakeGraph().Object(GroupsWhere("displayName eq 'Pilot'"), Page()).Object(GroupsWhere("mailNickname eq 'pilot'"), Page());
        var policy = Policy("CA011", On(ResourceRegistry.Group, SourceGroup));
        var plan = await Matcher.BuildAsync(SourceWithGroup("Pilot", "pilot"), destination, [policy], [policy]);
        plan.Find(ResourceRegistry.Group, SourceGroup)!.MapTo(new ObjectRef(DestinationGroup, "Pilot, devices \"ring 1\""));
        var csv = MappingCsv.Write(plan);

        var fresh = await Matcher.BuildAsync(SourceWithGroup("Pilot", "pilot"), destination, [policy], [policy]);
        var result = MappingCsv.Read(fresh, csv);

        Assert.Equal(1, result.Applied);
        Assert.Empty(result.Problems);
        var mapping = fresh.Find(ResourceRegistry.Group, SourceGroup)!;
        Assert.Equal(MatchKind.Manual, mapping.Kind);
        Assert.Equal("Pilot, devices \"ring 1\"", mapping.Destination!.DisplayName);
    }

    [Fact]
    public void Csv_import_reports_rows_it_cannot_use()
    {
        var plan = new MappingPlan([]);
        var csv = "Type,SourceId,SourceName,Decision,DestinationId,DestinationName\ngroup,unknown,Old group,map,x,X\n";

        var result = MappingCsv.Read(plan, csv);

        Assert.Equal(0, result.Applied);
        Assert.Contains("isn't used", Assert.Single(result.Problems));
    }
}
