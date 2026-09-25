using Movewise.Core.Access;

namespace Movewise.Core.Tests;

public class RoleRequirementsTests
{
    [Fact]
    public void Global_administrator_is_enough_on_either_side()
    {
        Assert.True(RoleRequirements.Check(TenantRole.Source, [DirectoryRoles.GlobalAdministrator]).IsSufficient);
        Assert.True(RoleRequirements.Check(TenantRole.Destination, [DirectoryRoles.GlobalAdministrator]).IsSufficient);
    }

    [Fact]
    public void Source_needs_global_reader()
    {
        Assert.True(RoleRequirements.Check(TenantRole.Source, [DirectoryRoles.GlobalReader]).IsSufficient);

        var result = RoleRequirements.Check(TenantRole.Source, []);
        Assert.False(result.IsSufficient);
        Assert.Equal(new[] { "Global Reader" }, result.Missing);
    }

    [Fact]
    public void Global_reader_is_not_enough_for_the_destination()
    {
        var result = RoleRequirements.Check(TenantRole.Destination, [DirectoryRoles.GlobalReader]);

        Assert.False(result.IsSufficient);
        Assert.Equal(5, result.Missing.Count);
    }

    [Fact]
    public void Destination_lists_only_the_missing_service_roles()
    {
        var result = RoleRequirements.Check(TenantRole.Destination,
        [
            DirectoryRoles.ConditionalAccessAdministrator,
            DirectoryRoles.IntuneAdministrator,
            DirectoryRoles.ExchangeAdministrator,
        ]);

        Assert.False(result.IsSufficient);
        Assert.Equal(new[] { "Compliance Administrator", "Teams Administrator" }, result.Missing);
    }
}
